/** `/connect`, `/oauth/callback`, `/pair/exchange`: the OAuth 2.1 + PKCE flow and the hand-over to the local service (PRD §5.3). */
import { indexSlug } from "./config";
import { constantTimeEqual, pkceChallenge, randomToken, sha256Hex } from "./crypto";
import { HOST_PORT_MAX, HOST_PORT_MIN, allowedSlugs, isConfigured, type Env } from "./env";
import { EVENTS, KickApiError, authorizeUrl, exchangeCode, getCurrentUser, getOwnChannel, revokeToken, subscribeEvents, type SubscriptionRecord } from "./kick-api";
import { closedBetaPage, errorPage, pairedPage } from "./pages";

const PAIR_HASH = /^[0-9a-f]{64}$/;

function pending(env: Env) {
  return env.PENDING.get(env.PENDING.idFromName("pending"));
}

export function channelStub(env: Env, userId: string) {
  return env.CHANNEL.get(env.CHANNEL.idFromName(userId));
}

/** Starts the flow: remembers the pair hash and the loopback port under a random state, redirects to Kick. */
export async function handleConnect(request: Request, env: Env): Promise<Response> {
  if (!isConfigured(env)) {
    return json({ error: "relay_not_configured", detail: "the Kick app is not configured on this relay yet" }, 503);
  }
  const url = new URL(request.url);
  const pair = (url.searchParams.get("pair") ?? "").toLowerCase();
  const port = Number(url.searchParams.get("port") ?? "");
  if (!PAIR_HASH.test(pair)) {
    return json({ error: "bad_request", detail: "pair must be the hex SHA-256 of the pair verifier" }, 400);
  }
  if (!Number.isInteger(port) || port < HOST_PORT_MIN || port > HOST_PORT_MAX) {
    return json({ error: "bad_request", detail: `port must be between ${HOST_PORT_MIN} and ${HOST_PORT_MAX}` }, 400);
  }
  const verifier = randomToken(32);
  const state = await pending(env).createState(pair, port, verifier);
  return Response.redirect(authorizeUrl(env, state, await pkceChallenge(verifier)), 302);
}

/** Kick sends the user back here. Exchanges the code, checks the beta list, subscribes the webhooks, installs the channel, issues the pairing code. */
export async function handleCallback(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const state = url.searchParams.get("state") ?? "";
  const code = url.searchParams.get("code") ?? "";
  const kickError = url.searchParams.get("error");
  const pendingState = state ? await pending(env).consumeState(state) : null;
  if (!pendingState) {
    return errorPage(400, "the authorisation expired or was already used (state)");
  }
  if (kickError) {
    return errorPage(400, `Kick: ${kickError}`);
  }
  if (!code) {
    return errorPage(400, "missing code");
  }

  try {
    const tokens = await exchangeCode(env, code, pendingState.verifier);
    const user = await getCurrentUser(tokens.access_token);
    const channel = await getOwnChannel(tokens.access_token);
    const allowed = allowedSlugs(env);
    if (allowed.length > 0 && !allowed.includes(channel.slug)) {
      await revokeToken(tokens.access_token, "access_token");
      await revokeToken(tokens.refresh_token, "refresh_token");
      return closedBetaPage(channel.slug);
    }

    // Subscribe first, then persist: a channel stored without subscriptions would be silent until the daily alarm.
    const subscriptions: SubscriptionRecord[] = [];
    try {
      for (const r of await subscribeEvents(tokens.access_token, EVENTS)) {
        if (r.subscription_id) {
          subscriptions.push({ id: r.subscription_id, event: r.name, version: r.version });
        }
      }
    } catch (error) {
      // The alarm and every connection retry; the user should not have to authorise twice for a transient failure.
      console.log("relay: subscribing at callback failed", error instanceof KickApiError ? error.status : "error");
    }

    const userId = channel.broadcaster_user_id || user.user_id;
    await channelStub(env, userId).install({ user_id: userId, slug: channel.slug, username: user.name, tokens, subscriptions });
    await indexSlug(env, channel.slug, userId);
    const pairCode = await pending(env).createPairCode(pendingState.pair, userId);
    return pairedPage(pairCode, pendingState.port, channel.slug);
  } catch (error) {
    if (error instanceof KickApiError) {
      console.log("relay: callback failed", error.code, error.status);
      return errorPage(502, `Kick answered ${error.status} (${error.code})`);
    }
    throw error;
  }
}

/** The local service proves it started the flow (it holds the verifier) and gets its device token. */
export async function handlePairExchange(request: Request, env: Env): Promise<Response> {
  let body: { code?: unknown; pair_verifier?: unknown };
  try {
    body = (await request.json()) as typeof body;
  } catch {
    return json({ error: "bad_request", detail: "JSON body expected" }, 400);
  }
  const code = typeof body.code === "string" ? body.code.trim() : "";
  const verifier = typeof body.pair_verifier === "string" ? body.pair_verifier : "";
  if (!code || !verifier) {
    return json({ error: "bad_request", detail: "code and pair_verifier are required" }, 400);
  }
  const pairCode = await pending(env).consumePairCode(code);
  if (!pairCode) {
    return json({ error: "invalid_code", detail: "unknown, expired or already used code" }, 404);
  }
  if (!constantTimeEqual(await sha256Hex(verifier), pairCode.pair)) {
    // The code is burnt either way: whoever presented it did not start this flow.
    return json({ error: "verifier_mismatch", detail: "pair_verifier does not match the flow this code belongs to" }, 403);
  }
  const deviceToken = `${pairCode.user_id}.${randomToken(32)}`;
  const channel = await channelStub(env, pairCode.user_id).issueDevice(await sha256Hex(deviceToken));
  return json({ device_token: deviceToken, channel });
}

/** `Authorization: Bearer <user_id>.<random>` → the channel stub and the token hash, or null when malformed / unknown. */
export async function authenticateDevice(request: Request, env: Env): Promise<{ userId: string; hash: string } | null> {
  const header = request.headers.get("authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
  const dot = token.indexOf(".");
  if (dot <= 0 || dot === token.length - 1 || token.length > 200) {
    return null;
  }
  const userId = token.slice(0, dot);
  if (!/^[0-9]+$/.test(userId)) {
    return null;
  }
  const hash = await sha256Hex(token);
  if (!(await channelStub(env, userId).hasDevice(hash))) {
    return null;
  }
  return { userId, hash };
}

export function json(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" } });
}
