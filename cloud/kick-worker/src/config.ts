import { normalizeSlug } from "./env";
/**
 * Per-channel configuration replicated in Workers KV so the webhook route can drop irrelevant chat messages before
 * touching the channel's Durable Object (PRD §4, decision 2). Keys: `config:<broadcaster_user_id>`, `slug:<slug>`.
 */
import type { Env } from "./env";
import type { ConfigMessage } from "./protocol";

export interface ChannelConfig {
  prefix: string;
  commands: { name: string; aliases: string[] }[];
  public_page: boolean;
  forward: { stream_status: boolean; redemptions: boolean; redemption_filter: string[] };
  updated_at: number;
}

export function configKey(userId: string): string {
  return `config:${userId}`;
}

export function slugKey(slug: string): string {
  return `slug:${normalizeSlug(slug)}`;
}

export async function loadConfig(env: Env, userId: string): Promise<ChannelConfig | null> {
  const stored = await env.CONFIG.get(configKey(userId), "json").catch(() => null);
  return stored && typeof stored === "object" ? (stored as ChannelConfig) : null;
}

export async function saveConfig(env: Env, userId: string, message: ConfigMessage, now = Date.now()): Promise<ChannelConfig> {
  const config: ChannelConfig = {
    prefix: message.prefix,
    commands: message.commands,
    public_page: message.public_page,
    forward: message.forward,
    updated_at: now,
  };
  await env.CONFIG.put(configKey(userId), JSON.stringify(config));
  return config;
}

export async function deleteConfig(env: Env, userId: string, slug: string | null): Promise<void> {
  await env.CONFIG.delete(configKey(userId));
  if (slug) {
    const owner = await env.CONFIG.get(slugKey(slug));
    if (owner === userId) {
      await env.CONFIG.delete(slugKey(slug));
    }
  }
}

/** Keeps `slug → user_id` current; the slug can change, so webhooks refresh it (one KV write only when it differs). */
export async function indexSlug(env: Env, slug: string, userId: string): Promise<void> {
  const key = slugKey(slug);
  const current = await env.CONFIG.get(key);
  if (current !== userId) {
    await env.CONFIG.put(key, userId);
  }
}

export async function resolveSlug(env: Env, slug: string): Promise<string | null> {
  return env.CONFIG.get(slugKey(slug));
}

export interface MatchedCommand {
  command: string;
  args: string;
}

/** `!mazo foo bar` → `{ command: "deck", args: "foo bar" }` when `mazo` is an alias of `deck`; null for anything else. */
export function matchCommand(config: ChannelConfig, content: string): MatchedCommand | null {
  const text = content.trim();
  if (!config.prefix || !text.startsWith(config.prefix)) {
    return null;
  }
  const rest = text.slice(config.prefix.length);
  const match = /^([a-z0-9_]+)(?:\s+(.*))?$/is.exec(rest);
  if (!match) {
    return null;
  }
  const word = (match[1] ?? "").toLowerCase();
  for (const command of config.commands) {
    if (command.name === word || command.aliases.includes(word)) {
      return { command: command.name, args: (match[2] ?? "").trim().slice(0, 200) };
    }
  }
  return null;
}
