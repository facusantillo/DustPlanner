/**
 * The only module that talks to Kick (PRD §2, confirmed against the OpenAPI in K0). Every function takes the token it
 * needs; nothing here stores anything. Tests intercept these hosts with `fetchMock`.
 */
import type { Env } from "./env";

export const KICK_ID_BASE = "https://id.kick.com";
export const KICK_API_BASE = "https://api.kick.com";

/** The five scopes DustPlanner asks for, all at once (PRD §2.2). */
export const REQUIRED_SCOPES = ["user:read", "channel:read", "events:subscribe", "chat:write", "channel:rewards:read"] as const;

/** The webhooks the relay subscribes every channel to (PRD §2.3). */
export const EVENTS = [
  { name: "chat.message.sent", version: 1 },
  { name: "livestream.status.updated", version: 1 },
  { name: "channel.reward.redemption.updated", version: 1 },
] as const;

export interface TokenSet {
  access_token: string;
  refresh_token: string;
  /** Unix ms when the access token expires. */
  expires_at: number;
  scope: string;
}

export class KickApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    detail: string,
  ) {
    super(`${code}: ${detail}`);
  }

  /** The refresh token is dead (revoked at Kick, expired, or rotated away): the channel must reconnect. */
  get isInvalidGrant(): boolean {
    return this.code === "invalid_grant";
  }
}

export function authorizeUrl(env: Env, state: string, codeChallenge: string): string {
  const url = new URL("/oauth/authorize", KICK_ID_BASE);
  url.searchParams.set("client_id", env.KICK_CLIENT_ID);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("redirect_uri", redirectUri(env));
  url.searchParams.set("state", state);
  url.searchParams.set("scope", REQUIRED_SCOPES.join(" "));
  url.searchParams.set("code_challenge", codeChallenge);
  url.searchParams.set("code_challenge_method", "S256");
  return url.toString();
}

export function redirectUri(env: Env): string {
  return `${env.PUBLIC_BASE_URL.replace(/\/$/, "")}/oauth/callback`;
}

export async function exchangeCode(env: Env, code: string, codeVerifier: string, now = Date.now()): Promise<TokenSet> {
  return tokenRequest(
    new URLSearchParams({
      grant_type: "authorization_code",
      code,
      client_id: env.KICK_CLIENT_ID,
      client_secret: env.KICK_CLIENT_SECRET,
      redirect_uri: redirectUri(env),
      code_verifier: codeVerifier,
    }),
    now,
  );
}

export async function refreshTokens(env: Env, refreshToken: string, now = Date.now()): Promise<TokenSet> {
  return tokenRequest(
    new URLSearchParams({
      grant_type: "refresh_token",
      refresh_token: refreshToken,
      client_id: env.KICK_CLIENT_ID,
      client_secret: env.KICK_CLIENT_SECRET,
    }),
    now,
  );
}

async function tokenRequest(form: URLSearchParams, now: number): Promise<TokenSet> {
  const response = await fetch(`${KICK_ID_BASE}/oauth/token`, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded", accept: "application/json" },
    body: form.toString(),
  });
  const body = (await response.json().catch(() => ({}))) as Record<string, unknown>;
  if (!response.ok) {
    throw new KickApiError(response.status, typeof body["error"] === "string" ? body["error"] : "token_error", String(body["error_description"] ?? response.status));
  }
  if (typeof body["access_token"] !== "string" || typeof body["refresh_token"] !== "string") {
    throw new KickApiError(response.status, "token_error", "token response without access_token / refresh_token");
  }
  const expiresIn = typeof body["expires_in"] === "number" ? body["expires_in"] : 3600;
  return {
    access_token: body["access_token"],
    refresh_token: body["refresh_token"],
    expires_at: now + expiresIn * 1000,
    scope: typeof body["scope"] === "string" ? body["scope"] : "",
  };
}

export async function revokeToken(token: string, hint: "access_token" | "refresh_token"): Promise<void> {
  const url = new URL("/oauth/revoke", KICK_ID_BASE);
  url.searchParams.set("token", token);
  url.searchParams.set("token_hint_type", hint);
  // Best effort: a failed revocation must not block a disconnect.
  await fetch(url.toString(), { method: "POST" }).catch(() => undefined);
}

/** Scopes the token lacks, compared with the five the relay needs. */
export function missingScopes(scope: string): string[] {
  const granted = new Set(scope.split(/[\s,]+/).filter(Boolean));
  return REQUIRED_SCOPES.filter((s) => !granted.has(s));
}

export interface KickUser {
  user_id: string;
  name: string;
}

/** `GET /public/v1/users` without ids: the token's own user. The email it carries is never read. */
export async function getCurrentUser(accessToken: string): Promise<KickUser> {
  const data = await apiGet<{ user_id?: number | string; name?: string }[]>("/public/v1/users", accessToken);
  const first = data[0];
  if (!first || first.user_id === undefined) {
    throw new KickApiError(502, "users_empty", "GET /public/v1/users returned no user");
  }
  return { user_id: String(first.user_id), name: first.name ?? "" };
}

export interface KickChannel {
  broadcaster_user_id: string;
  slug: string;
}

/** `GET /public/v1/channels` without parameters: the token's own channel (confirmed in K0). */
export async function getOwnChannel(accessToken: string): Promise<KickChannel> {
  const data = await apiGet<{ broadcaster_user_id?: number | string; slug?: string }[]>("/public/v1/channels", accessToken);
  const first = data[0];
  if (!first || first.broadcaster_user_id === undefined || !first.slug) {
    throw new KickApiError(502, "channels_empty", "GET /public/v1/channels returned no channel");
  }
  return { broadcaster_user_id: String(first.broadcaster_user_id), slug: first.slug.toLowerCase() };
}

export interface SubscriptionRecord {
  id: string;
  event: string;
  version: number;
}

export async function listSubscriptions(accessToken: string): Promise<SubscriptionRecord[]> {
  const data = await apiGet<{ id?: string; event?: string; version?: number }[]>("/public/v1/events/subscriptions", accessToken);
  return data.filter((s) => s.id && s.event).map((s) => ({ id: String(s.id), event: String(s.event), version: s.version ?? 1 }));
}

export interface SubscribeResult {
  name: string;
  version: number;
  subscription_id?: string;
  error?: string;
}

/** With a user token the broadcaster is inferred from the token (confirmed in K0): no `broadcaster_user_id` needed. */
export async function subscribeEvents(accessToken: string, events: readonly { name: string; version: number }[]): Promise<SubscribeResult[]> {
  const response = await fetch(`${KICK_API_BASE}/public/v1/events/subscriptions`, {
    method: "POST",
    headers: { authorization: `Bearer ${accessToken}`, "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ events: events.map((e) => ({ name: e.name, version: e.version })), method: "webhook" }),
  });
  if (!response.ok) {
    throw new KickApiError(response.status, response.status === 401 ? "unauthorized" : "subscribe_failed", `POST /public/v1/events/subscriptions → ${response.status}`);
  }
  const body = (await response.json()) as { data?: SubscribeResult[] };
  return body.data ?? [];
}

export async function deleteSubscriptions(accessToken: string, ids: string[]): Promise<void> {
  if (ids.length === 0) {
    return;
  }
  const url = new URL("/public/v1/events/subscriptions", KICK_API_BASE);
  for (const id of ids) {
    url.searchParams.append("id", id);
  }
  await fetch(url.toString(), { method: "DELETE", headers: { authorization: `Bearer ${accessToken}` } }).catch(() => undefined);
}

export interface ChatSendOutcome {
  status: number;
}

/** `POST /public/v1/chat` as the bot: the message goes to the channel attached to the token (confirmed in K0). */
export async function sendChat(accessToken: string, content: string, replyToMessageId?: string): Promise<ChatSendOutcome> {
  const payload: Record<string, unknown> = { type: "bot", content };
  if (replyToMessageId) {
    payload["reply_to_message_id"] = replyToMessageId;
  }
  const response = await fetch(`${KICK_API_BASE}/public/v1/chat`, {
    method: "POST",
    headers: { authorization: `Bearer ${accessToken}`, "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(payload),
  });
  // The body is irrelevant to the relay and must never be logged.
  await response.body?.cancel().catch(() => undefined);
  return { status: response.status };
}

/** The PEM public key Kick signs webhooks with. Fetched, never hard-coded: Kick may rotate it. */
export async function fetchPublicKey(): Promise<string> {
  const response = await fetch(`${KICK_API_BASE}/public/v1/public-key`, { headers: { accept: "application/json" } });
  if (!response.ok) {
    throw new KickApiError(response.status, "public_key_unavailable", `GET /public/v1/public-key → ${response.status}`);
  }
  const body = (await response.json()) as { data?: { public_key?: string } };
  const pem = body.data?.public_key;
  if (!pem || !pem.includes("PUBLIC KEY")) {
    throw new KickApiError(502, "public_key_invalid", "public key response without a PEM block");
  }
  return pem;
}

async function apiGet<T>(path: string, accessToken: string): Promise<T> {
  const response = await fetch(`${KICK_API_BASE}${path}`, { headers: { authorization: `Bearer ${accessToken}`, accept: "application/json" } });
  if (!response.ok) {
    throw new KickApiError(response.status, response.status === 401 ? "unauthorized" : "api_error", `GET ${path} → ${response.status}`);
  }
  const body = (await response.json()) as { data?: T };
  return (body.data ?? ([] as unknown)) as T;
}
