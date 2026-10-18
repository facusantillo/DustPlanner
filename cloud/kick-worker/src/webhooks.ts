/**
 * `POST /webhooks/kick` (PRD §5.3): verify, drop what nobody asked for, hand the rest to the channel's Durable Object.
 * Answers 200 whenever the signature is valid, even with nothing to do, so Kick never unsubscribes the app (R2).
 * Nothing from the body is logged.
 */
import { indexSlug, loadConfig, matchCommand } from "./config";
import { importRsaPublicKey, verifyKickSignature } from "./crypto";
import type { Env } from "./env";
import { fetchPublicKey } from "./kick-api";
import { channelStub } from "./oauth";
import type { DeliveredEvent } from "./channel-do";

/** Events older than this are acknowledged and dropped (R3: webhooks can lag; stale commands are worse than none). */
export const MAX_EVENT_AGE_MS = 10 * 60 * 1000;
/** After a signature failure the key is fetched again at most this often; inside the window the event is refused. */
export const KEY_REFETCH_INTERVAL_MS = 5 * 60 * 1000;
const KEY_KV = "kick:public_key";

interface KeyCache {
  pem: string;
  key: CryptoKey;
  fetchedAt: number;
}

/** Per-isolate cache; KV keeps the PEM across isolates so a cold start does not hit Kick's key endpoint. */
let cache: KeyCache | null = null;
let lastRefetchAt = 0;

export function resetKeyCacheForTests(): void {
  cache = null;
  lastRefetchAt = 0;
}

async function currentKey(env: Env, now: number): Promise<KeyCache | null> {
  if (cache) {
    return cache;
  }
  const stored = await env.CONFIG.get(KEY_KV).catch(() => null);
  if (stored) {
    try {
      cache = { pem: stored, key: await importRsaPublicKey(stored), fetchedAt: now };
      return cache;
    } catch {
      // fall through to a fresh fetch
    }
  }
  // A cold start is not a failure: it does not consume the once-per-window refetch.
  return refetchKey(env, now, false);
}

async function refetchKey(env: Env, now: number, afterFailure = true): Promise<KeyCache | null> {
  if (afterFailure) {
    lastRefetchAt = now;
  }
  try {
    const pem = await fetchPublicKey();
    cache = { pem, key: await importRsaPublicKey(pem), fetchedAt: now };
    await env.CONFIG.put(KEY_KV, pem).catch(() => undefined);
    return cache;
  } catch {
    console.log("relay: could not fetch Kick's public key");
    return null;
  }
}

export interface WebhookOutcome {
  status: number;
  action: "rejected" | "stale" | "ignored" | "delivered" | "duplicate" | "no_device" | "stored";
}

export async function handleWebhook(request: Request, env: Env, now = Date.now()): Promise<Response> {
  const outcome = await processWebhook(request, env, now);
  return new Response(JSON.stringify(outcome), { status: outcome.status, headers: { "content-type": "application/json" } });
}

export async function processWebhook(request: Request, env: Env, now: number): Promise<WebhookOutcome> {
  const messageId = request.headers.get("kick-event-message-id") ?? "";
  const timestamp = request.headers.get("kick-event-message-timestamp") ?? "";
  const signature = request.headers.get("kick-event-signature") ?? "";
  const eventType = request.headers.get("kick-event-type") ?? "";
  const eventVersion = request.headers.get("kick-event-version") ?? "1";
  if (!messageId || !timestamp || !signature || !eventType) {
    return { status: 400, action: "rejected" };
  }
  const rawBody = await request.text();

  let keyCache = await currentKey(env, now);
  let valid = keyCache ? await verifyKickSignature(keyCache.key, messageId, timestamp, rawBody, signature) : false;
  if (!valid) {
    // Maybe Kick rotated the key: ask once per window, never on every bad request.
    if (now - lastRefetchAt >= KEY_REFETCH_INTERVAL_MS) {
      keyCache = await refetchKey(env, now);
      valid = keyCache ? await verifyKickSignature(keyCache.key, messageId, timestamp, rawBody, signature) : false;
    }
    if (!valid) {
      return { status: 401, action: "rejected" };
    }
  }

  const sentAt = Date.parse(timestamp);
  if (!Number.isFinite(sentAt) || Math.abs(now - sentAt) > MAX_EVENT_AGE_MS) {
    return { status: 200, action: "stale" };
  }

  let body: Record<string, unknown>;
  try {
    body = JSON.parse(rawBody) as Record<string, unknown>;
  } catch {
    return { status: 200, action: "ignored" };
  }
  const broadcaster = (body["broadcaster"] ?? {}) as Record<string, unknown>;
  const userId = broadcaster["user_id"] !== undefined ? String(broadcaster["user_id"]) : "";
  if (!userId) {
    return { status: 200, action: "ignored" };
  }
  if (typeof broadcaster["channel_slug"] === "string" && broadcaster["channel_slug"]) {
    await indexSlug(env, broadcaster["channel_slug"], userId).catch(() => undefined);
  }

  const event = await selectEvent(env, eventType, eventVersion, messageId, timestamp, userId, body);
  if (!event) {
    return { status: 200, action: "ignored" };
  }
  const result = await channelStub(env, userId).deliver(event, now);
  return { status: 200, action: result === "forwarded" ? "delivered" : result };
}

/** The filter that keeps chat out of the Durable Object: only declared commands, never the bot's own lines. */
async function selectEvent(env: Env, type: string, version: string, messageId: string, timestamp: string, userId: string, body: Record<string, unknown>): Promise<DeliveredEvent | null> {
  if (version !== "1") {
    return null;
  }
  switch (type) {
    case "livestream.status.updated":
      // Always delivered: the Durable Object keeps the last state for the `hello`, forwarding depends on the config.
      return {
        type: "stream.status",
        id: messageId,
        is_live: body["is_live"] === true,
        title: typeof body["title"] === "string" ? body["title"] : null,
        started_at: typeof body["started_at"] === "string" ? body["started_at"] : null,
        ended_at: typeof body["ended_at"] === "string" ? body["ended_at"] : null,
      };
    case "chat.message.sent": {
      const sender = (body["sender"] ?? {}) as Record<string, unknown>;
      const senderId = sender["user_id"] !== undefined ? String(sender["user_id"]) : "";
      if (!senderId || senderId === env.BOT_USER_ID) {
        return null;
      }
      const config = await loadConfig(env, userId);
      if (!config || typeof body["content"] !== "string") {
        return null;
      }
      const matched = matchCommand(config, body["content"]);
      if (!matched) {
        return null;
      }
      const identity = (sender["identity"] ?? {}) as Record<string, unknown>;
      const badges = Array.isArray(identity["badges"])
        ? identity["badges"].map((b) => (b && typeof b === "object" ? String((b as Record<string, unknown>)["type"] ?? "") : "")).filter(Boolean)
        : [];
      return {
        type: "chat.command",
        id: typeof body["message_id"] === "string" && body["message_id"] ? body["message_id"] : messageId,
        command: matched.command,
        args: matched.args,
        sender: { user_id: senderId, username: typeof sender["username"] === "string" ? sender["username"] : "", badges },
        at: typeof body["created_at"] === "string" ? body["created_at"] : timestamp,
      };
    }
    case "channel.reward.redemption.updated": {
      const config = await loadConfig(env, userId);
      if (!config?.forward.redemptions) {
        return null;
      }
      const status = body["status"];
      if (status !== "pending" && status !== "accepted") {
        return null;
      }
      const reward = (body["reward"] ?? {}) as Record<string, unknown>;
      const rewardId = reward["id"] !== undefined ? String(reward["id"]) : "";
      if (!rewardId || (config.forward.redemption_filter.length > 0 && !config.forward.redemption_filter.includes(rewardId))) {
        return null;
      }
      const redeemer = (body["redeemer"] ?? {}) as Record<string, unknown>;
      return {
        type: "reward.redemption",
        id: body["id"] !== undefined ? String(body["id"]) : messageId,
        status,
        reward: { id: rewardId, title: typeof reward["title"] === "string" ? reward["title"] : "", cost: typeof reward["cost"] === "number" ? reward["cost"] : 0 },
        redeemer: { username: typeof redeemer["username"] === "string" ? redeemer["username"] : "" },
        user_input: typeof body["user_input"] === "string" ? body["user_input"].slice(0, 500) : "",
      };
    }
    default:
      return null;
  }
}
