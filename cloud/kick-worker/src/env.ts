import type { ChannelDO } from "./channel-do";
import type { PendingDO } from "./pending-do";

/** Bindings and variables of the relay. Secrets come from `wrangler secret` (or GitHub Secrets in the deploy workflow). */
export interface Env {
  // [vars]
  KICK_CLIENT_ID: string;
  BOT_USER_ID: string;
  ALLOWED_SLUGS: string;
  PUBLIC_BASE_URL: string;
  RELAY_VERSION: string;
  // secrets
  KICK_CLIENT_SECRET: string;
  TOKEN_ENCRYPTION_KEY: string;
  // bindings
  CONFIG: KVNamespace;
  CHANNEL: DurableObjectNamespace<ChannelDO>;
  PENDING: DurableObjectNamespace<PendingDO>;
}

/** The relay refuses to start an OAuth flow until the Kick app exists (VERIFICACION K1). */
export function isConfigured(env: Env): boolean {
  return Boolean(env.KICK_CLIENT_ID) && env.KICK_CLIENT_ID !== "REPLACE_ME" && Boolean(env.KICK_CLIENT_SECRET) && Boolean(env.TOKEN_ENCRYPTION_KEY);
}

/**
 * Kick derives a channel's slug from the username by lower-casing it and turning `_` into `-` (`tsukuru_tv` →
 * `tsukuru-tv`). Compare and store slugs in that shape so the list can be written either way.
 */
export function normalizeSlug(slug: string): string {
  return slug.trim().toLowerCase().replace(/_/g, "-");
}

export function allowedSlugs(env: Env): string[] {
  return (env.ALLOWED_SLUGS ?? "")
    .split(",")
    .map(normalizeSlug)
    .filter((s) => s.length > 0);
}

/** Empty list = open to everyone; otherwise the channel's slug must be on it. */
export function isAllowedChannel(env: Env, slug: string): boolean {
  const allowed = allowedSlugs(env);
  return allowed.length === 0 || allowed.includes(normalizeSlug(slug));
}

/** Loopback ports the local service may listen on (8765 and the fallbacks); anything else is refused. */
export const HOST_PORT_MIN = 8765;
export const HOST_PORT_MAX = 8774;
