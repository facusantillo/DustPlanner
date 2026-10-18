import { DurableObject } from "cloudflare:workers";
import type { Env } from "./env";
import { randomToken } from "./crypto";

/** How long an authorisation may take between /connect and /oauth/callback. */
export const OAUTH_STATE_TTL_MS = 10 * 60 * 1000;

/** How long the one-time pairing code shown after the callback stays valid. */
export const PAIR_CODE_TTL_MS = 2 * 60 * 1000;

export interface PendingState {
  pair: string;
  port: number;
  verifier: string;
}

export interface PendingPairCode {
  pair: string;
  user_id: string;
}

/**
 * One global Durable Object (`idFromName("pending")`) holding short-lived, single-use secrets of the OAuth dance:
 * the `state` + PKCE verifier between /connect and the callback, and the pairing code the callback hands to the local
 * service. Strongly consistent, unlike KV, so a code can never be used twice.
 */
export class PendingDO extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => {
      this.ctx.storage.sql.exec(`
        CREATE TABLE IF NOT EXISTS oauth_state (
          state TEXT PRIMARY KEY, pair TEXT NOT NULL, port INTEGER NOT NULL, verifier TEXT NOT NULL, expires_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS pair_code (
          code TEXT PRIMARY KEY, pair TEXT NOT NULL, user_id TEXT NOT NULL, expires_at INTEGER NOT NULL
        );
      `);
    });
  }

  /** Stores the pair hash, the loopback port and the PKCE verifier under a fresh random `state`; returns the state. */
  async createState(pair: string, port: number, verifier: string, now = Date.now()): Promise<string> {
    this.prune(now);
    const state = randomToken(32);
    this.ctx.storage.sql.exec("INSERT INTO oauth_state(state, pair, port, verifier, expires_at) VALUES (?, ?, ?, ?, ?)", state, pair, port, verifier, now + OAUTH_STATE_TTL_MS);
    return state;
  }

  /** Returns and deletes the state (single use); null when unknown or expired. */
  async consumeState(state: string, now = Date.now()): Promise<PendingState | null> {
    this.prune(now);
    const row = this.ctx.storage.sql.exec<{ pair: string; port: number; verifier: string }>("SELECT pair, port, verifier FROM oauth_state WHERE state = ?", state).toArray()[0];
    if (!row) {
      return null;
    }
    this.ctx.storage.sql.exec("DELETE FROM oauth_state WHERE state = ?", state);
    return { pair: row.pair, port: Number(row.port), verifier: row.verifier };
  }

  /** A short, single-use code bound to the pair hash the flow started with and to the channel that authorised. */
  async createPairCode(pair: string, userId: string, now = Date.now()): Promise<string> {
    this.prune(now);
    // 16 random bytes → 22 url-safe characters: easy to paste by hand, impossible to guess within two minutes.
    const code = randomToken(16);
    this.ctx.storage.sql.exec("INSERT INTO pair_code(code, pair, user_id, expires_at) VALUES (?, ?, ?, ?)", code, pair, userId, now + PAIR_CODE_TTL_MS);
    return code;
  }

  async consumePairCode(code: string, now = Date.now()): Promise<PendingPairCode | null> {
    this.prune(now);
    const row = this.ctx.storage.sql.exec<{ pair: string; user_id: string }>("SELECT pair, user_id FROM pair_code WHERE code = ?", code).toArray()[0];
    if (!row) {
      return null;
    }
    this.ctx.storage.sql.exec("DELETE FROM pair_code WHERE code = ?", code);
    return { pair: row.pair, user_id: row.user_id };
  }

  private prune(now: number): void {
    this.ctx.storage.sql.exec("DELETE FROM oauth_state WHERE expires_at <= ?", now);
    this.ctx.storage.sql.exec("DELETE FROM pair_code WHERE expires_at <= ?", now);
  }
}
