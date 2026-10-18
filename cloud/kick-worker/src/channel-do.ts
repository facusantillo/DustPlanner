import { DurableObject } from "cloudflare:workers";
import { deleteConfig, saveConfig, type ChannelConfig } from "./config";
import { TokenCipher } from "./crypto";
import type { Env } from "./env";
import {
  EVENTS,
  KickApiError,
  deleteSubscriptions,
  listSubscriptions,
  missingScopes,
  refreshTokens,
  revokeToken,
  sendChat,
  subscribeEvents,
  type ChatSendOutcome,
  type SubscriptionRecord,
  type TokenSet,
} from "./kick-api";
import {
  PING_FRAME,
  PONG_FRAME,
  parseServiceMessage,
  type ChannelInfo,
  type ChatSendMessage,
  type ConfigMessage,
  type LiveState,
  type RelayErrorCode,
  type RelayToService,
} from "./protocol";

/** Access tokens are renewed this long before they expire (PRD §5.3). */
const REFRESH_MARGIN_MS = 5 * 60 * 1000;
/** Daily: verify the webhook subscriptions and prune the dedupe tables. */
const ALARM_PERIOD_MS = 24 * 60 * 60 * 1000;
/** A channel whose refresh token died is deleted after this grace period unless it reconnects (Developer Agreement §5.2). */
const DELETE_GRACE_MS = 24 * 60 * 60 * 1000;
/** At most one chat message per second and per channel (PRD §5.3 security). */
const SEND_INTERVAL_MS = 1000;
const MESSAGE_DEDUPE_MS = 24 * 60 * 60 * 1000;
const REDEMPTION_DEDUPE_MS = 60 * 60 * 1000;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS channel (
  id INTEGER PRIMARY KEY CHECK (id = 1), user_id TEXT NOT NULL, slug TEXT NOT NULL, username TEXT NOT NULL,
  created_at INTEGER NOT NULL, needs_reconnect INTEGER NOT NULL DEFAULT 0, missing_scopes TEXT NOT NULL DEFAULT '', delete_after INTEGER
);
CREATE TABLE IF NOT EXISTS tokens (
  id INTEGER PRIMARY KEY CHECK (id = 1), access_enc TEXT NOT NULL, refresh_enc TEXT NOT NULL, expires_at INTEGER NOT NULL, scope TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS subscription (id TEXT PRIMARY KEY, event TEXT NOT NULL, version INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS device (hash TEXT PRIMARY KEY, created_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS live_state (id INTEGER PRIMARY KEY CHECK (id = 1), is_live INTEGER NOT NULL, title TEXT, started_at TEXT, ended_at TEXT);
CREATE TABLE IF NOT EXISTS public_deck (
  id INTEGER PRIMARY KEY CHECK (id = 1), name TEXT NOT NULL, deckstring TEXT NOT NULL, card_class TEXT, format TEXT, updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS processed_event (id TEXT PRIMARY KEY, expires_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
`;

export interface InstallInput {
  user_id: string;
  slug: string;
  username: string;
  tokens: TokenSet;
  subscriptions: SubscriptionRecord[];
}

export type DeliveredEvent =
  | { type: "chat.command"; id: string; command: string; args: string; sender: { user_id: string; username: string; badges: string[] }; at: string }
  | ({ type: "stream.status"; id: string } & LiveState)
  | { type: "reward.redemption"; id: string; status: "pending" | "accepted" | "rejected"; reward: { id: string; title: string; cost: number }; redeemer: { username: string }; user_input: string };

export interface ChannelStatus {
  installed: boolean;
  channel: ChannelInfo | null;
  needs_reconnect: boolean;
  missing_scopes: string[];
  has_tokens: boolean;
  subscriptions: SubscriptionRecord[];
  devices: number;
  sockets: number;
  live: LiveState;
  delete_after: number | null;
  public_deck: { name: string; deckstring: string; card_class: string | null; format: string | null; updated_at: number } | null;
}

class NeedsReconnect extends Error {
  constructor() {
    super("channel needs to reconnect");
  }
}

/**
 * One per connected channel (`idFromName(broadcaster_user_id)`). Owns the encrypted Kick tokens, the webhook
 * subscriptions, the paired devices and the WebSocket(s) to the streamer's local service. Never sees rank, record
 * or collection: it transports commands in and chat lines out. Hibernates between events.
 */
export class ChannelDO extends DurableObject<Env> {
  private refreshing: Promise<TokenSet> | null = null;
  private cipherPromise: Promise<TokenCipher> | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => {
      this.ctx.storage.sql.exec(SCHEMA);
    });
    // Answered by the runtime without waking a hibernated object.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair(PING_FRAME, PONG_FRAME));
  }

  // ------------------------------------------------------------------------------------------------ installation

  /** Called by the OAuth callback: stores everything the channel needs. Re-connecting an existing channel replaces it. */
  async install(input: InstallInput, now = Date.now()): Promise<{ missing_scopes: string[] }> {
    const cipher = await this.cipher();
    const missing = missingScopes(input.tokens.scope);
    const sql = this.ctx.storage.sql;
    sql.exec(
      `INSERT INTO channel(id, user_id, slug, username, created_at, needs_reconnect, missing_scopes, delete_after) VALUES (1, ?, ?, ?, ?, ?, ?, NULL)
       ON CONFLICT(id) DO UPDATE SET slug = excluded.slug, username = excluded.username, needs_reconnect = excluded.needs_reconnect,
         missing_scopes = excluded.missing_scopes, delete_after = NULL`,
      input.user_id,
      input.slug,
      input.username,
      now,
      missing.length > 0 ? 1 : 0,
      missing.join(" "),
    );
    await this.storeTokens(cipher, input.tokens);
    sql.exec("DELETE FROM subscription");
    for (const s of input.subscriptions) {
      sql.exec("INSERT OR REPLACE INTO subscription(id, event, version) VALUES (?, ?, ?)", s.id, s.event, s.version);
    }
    if ((await this.ctx.storage.getAlarm()) === null) {
      await this.ctx.storage.setAlarm(now + ALARM_PERIOD_MS);
    }
    this.broadcast({
      v: 1,
      type: "hello",
      channel: { user_id: input.user_id, slug: input.slug, username: input.username },
      needs_reconnect: missing.length > 0,
      missing_scopes: missing,
      live: this.liveState(),
      server_version: this.env.RELAY_VERSION,
      subscriptions: this.subscribedEvents(),
    });
    return { missing_scopes: missing };
  }

  /** Registers a paired device by the SHA-256 of its token; returns the channel it now belongs to. */
  async issueDevice(tokenHash: string, now = Date.now()): Promise<ChannelInfo> {
    const channel = this.channelInfo();
    if (!channel) {
      throw new Error("channel not installed");
    }
    this.ctx.storage.sql.exec("INSERT OR REPLACE INTO device(hash, created_at, last_seen_at) VALUES (?, ?, ?)", tokenHash, now, now);
    return channel;
  }

  async hasDevice(tokenHash: string): Promise<boolean> {
    return this.ctx.storage.sql.exec("SELECT 1 FROM device WHERE hash = ?", tokenHash).toArray().length > 0;
  }

  // ------------------------------------------------------------------------------------------------ websocket

  /** The Worker forwards `GET /ws` here with the device hash it computed; the socket is accepted with hibernation. */
  override async fetch(request: Request): Promise<Response> {
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
      return new Response("expected a WebSocket upgrade", { status: 426 });
    }
    const hash = request.headers.get("x-device-hash") ?? "";
    const channel = this.channelInfo();
    if (!channel || !(await this.hasDevice(hash))) {
      return new Response("unknown device", { status: 401 });
    }
    const now = Date.now();
    this.ctx.storage.sql.exec("UPDATE device SET last_seen_at = ? WHERE hash = ?", now, hash);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair) as [WebSocket, WebSocket];
    this.ctx.acceptWebSocket(server, [hash]);
    server.serializeAttachment({ hash, connected_at: now });
    const row = this.channelRow();
    this.send(server, {
      v: 1,
      type: "hello",
      channel,
      needs_reconnect: Boolean(row?.needs_reconnect),
      missing_scopes: row?.missing_scopes ? row.missing_scopes.split(" ").filter(Boolean) : [],
      live: this.liveState(),
      server_version: this.env.RELAY_VERSION,
      subscriptions: this.subscribedEvents(),
    });
    // A connecting service is a good moment to make sure Kick still delivers (PRD §5.3 renewal and retries). The
    // service is told when the set changed, so a card that said "no chat events" clears itself.
    this.ctx.waitUntil(this.ensureSubscriptions(true).catch(() => undefined));
    return new Response(null, { status: 101, webSocket: client });
  }

  override async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    const message = parseServiceMessage(raw);
    if (!message) {
      this.send(ws, { v: 1, type: "error", code: "bad_message", detail: "unrecognised message" });
      return;
    }
    const channel = this.channelInfo();
    if (!channel) {
      ws.close(4001, "channel gone");
      return;
    }
    switch (message.type) {
      case "ping":
        ws.send(PONG_FRAME);
        return;
      case "config":
        await this.applyConfig(channel.user_id, message);
        return;
      case "chat.send":
        await this.handleChatSend(ws, message);
        return;
      case "deck.publish":
        // Only while the page is on: a publish that races a config turning it off must not resurrect the deck.
        if (!this.localConfig()?.public_page) {
          return;
        }
        this.ctx.storage.sql.exec(
          `INSERT INTO public_deck(id, name, deckstring, card_class, format, updated_at) VALUES (1, ?, ?, ?, ?, ?)
           ON CONFLICT(id) DO UPDATE SET name = excluded.name, deckstring = excluded.deckstring, card_class = excluded.card_class, format = excluded.format, updated_at = excluded.updated_at`,
          message.name,
          message.deckstring,
          message.card_class,
          message.format,
          Date.now(),
        );
        return;
    }
  }

  override async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    ws.close(code, reason);
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    ws.close(1011, "error");
  }

  private async applyConfig(userId: string, message: ConfigMessage): Promise<void> {
    const config = await saveConfig(this.env, userId, message);
    this.ctx.storage.sql.exec("INSERT OR REPLACE INTO meta(key, value) VALUES ('config', ?)", JSON.stringify(config));
    if (!config.public_page) {
      this.ctx.storage.sql.exec("DELETE FROM public_deck");
    }
  }

  private localConfig(): ChannelConfig | null {
    const row = this.ctx.storage.sql.exec<{ value: string }>("SELECT value FROM meta WHERE key = 'config'").toArray()[0];
    return row ? (JSON.parse(row.value) as ChannelConfig) : null;
  }

  // ------------------------------------------------------------------------------------------------ chat sending

  private async handleChatSend(ws: WebSocket, message: ChatSendMessage): Promise<void> {
    const now = Date.now();
    const last = Number(this.metaGet("last_send_at") ?? 0);
    if (now - last < SEND_INTERVAL_MS) {
      this.send(ws, { v: 1, type: "error", code: "send_dropped", detail: "one message per second per channel" });
      return;
    }
    this.metaSet("last_send_at", String(now));

    let token: string;
    try {
      token = await this.accessToken();
    } catch (error) {
      this.send(ws, { v: 1, type: "error", code: error instanceof NeedsReconnect ? "needs_reconnect" : "kick_unavailable", detail: "no usable token" });
      return;
    }

    let outcome = await this.trySend(token, message);
    if (outcome.status === 401) {
      try {
        token = await this.accessToken(true);
      } catch (error) {
        this.send(ws, { v: 1, type: "error", code: error instanceof NeedsReconnect ? "needs_reconnect" : "kick_unavailable", detail: "token refresh failed" });
        return;
      }
      outcome = await this.trySend(token, message);
    }
    const status = outcome.status;
    if (status === 429) {
      this.send(ws, { v: 1, type: "error", code: "kick_rate_limited", detail: "Kick answered 429; message dropped" });
    } else if (status >= 500 || status === 0) {
      this.send(ws, { v: 1, type: "error", code: "kick_unavailable", detail: `Kick answered ${status || "network error"}; message dropped` });
    } else if (status >= 400) {
      // The reason is what turns "404" into "no bot user on the app" for whoever reads the card.
      this.send(ws, { v: 1, type: "error", code: "send_dropped", detail: `Kick refused the message (${status}${outcome.reason ? ": " + outcome.reason : ""})` });
    }
  }

  private async trySend(token: string, message: ChatSendMessage): Promise<ChatSendOutcome> {
    try {
      return await sendChat(token, message.text, message.reply_to_message_id);
    } catch {
      return { status: 0 };
    }
  }

  // ------------------------------------------------------------------------------------------------ events in

  /** From the webhook route, already filtered: deduplicated here (strongly consistent) and pushed to the service. */
  async deliver(event: DeliveredEvent, now = Date.now()): Promise<"forwarded" | "duplicate" | "no_device" | "stored"> {
    const dedupeKey = event.type === "reward.redemption" ? `redemption:${event.id}` : `event:${event.id}`;
    const ttl = event.type === "reward.redemption" ? REDEMPTION_DEDUPE_MS : MESSAGE_DEDUPE_MS;
    const sql = this.ctx.storage.sql;
    if (sql.exec("SELECT 1 FROM processed_event WHERE id = ? AND expires_at > ?", dedupeKey, now).toArray().length > 0) {
      return "duplicate";
    }
    sql.exec("INSERT OR REPLACE INTO processed_event(id, expires_at) VALUES (?, ?)", dedupeKey, now + ttl);

    const config = this.localConfig();
    let outgoing: RelayToService;
    switch (event.type) {
      case "stream.status":
        sql.exec(
          `INSERT INTO live_state(id, is_live, title, started_at, ended_at) VALUES (1, ?, ?, ?, ?)
           ON CONFLICT(id) DO UPDATE SET is_live = excluded.is_live, title = excluded.title, started_at = excluded.started_at, ended_at = excluded.ended_at`,
          event.is_live ? 1 : 0,
          event.title,
          event.started_at,
          event.ended_at,
        );
        if (!config?.forward.stream_status) {
          return "stored";
        }
        outgoing = { v: 1, type: "stream.status", is_live: event.is_live, title: event.title, started_at: event.started_at, ended_at: event.ended_at };
        break;
      case "chat.command":
        outgoing = { v: 1, type: "chat.command", message_id: event.id, command: event.command, args: event.args, sender: event.sender, at: event.at };
        break;
      case "reward.redemption":
        if (!config?.forward.redemptions) {
          return "stored";
        }
        if (config.forward.redemption_filter.length > 0 && !config.forward.redemption_filter.includes(event.reward.id)) {
          return "stored";
        }
        outgoing = { v: 1, type: "reward.redemption", id: event.id, status: event.status, reward: event.reward, redeemer: event.redeemer, user_input: event.user_input };
        break;
    }
    return this.broadcast(outgoing) > 0 ? "forwarded" : "no_device";
  }

  // ------------------------------------------------------------------------------------------------ tokens

  private async accessToken(force = false): Promise<string> {
    const row = this.tokenRow();
    if (!row) {
      throw new NeedsReconnect();
    }
    const now = Date.now();
    if (!force && row.expires_at - now > REFRESH_MARGIN_MS) {
      return (await this.cipher()).decrypt(row.access_enc);
    }
    // Serialised: Kick rotates the refresh token, so two renewals at once would invalidate one of them.
    if (!this.refreshing) {
      this.refreshing = this.refresh(row.refresh_enc).finally(() => {
        this.refreshing = null;
      });
    }
    return (await this.refreshing).access_token;
  }

  private async refresh(refreshEnc: string): Promise<TokenSet> {
    const cipher = await this.cipher();
    const refreshToken = await cipher.decrypt(refreshEnc);
    try {
      const tokens = await refreshTokens(this.env, refreshToken);
      await this.storeTokens(cipher, tokens);
      const missing = missingScopes(tokens.scope);
      if (missing.length > 0) {
        this.markNeedsReconnect(missing, null);
      }
      return tokens;
    } catch (error) {
      if (error instanceof KickApiError && error.isInvalidGrant) {
        // The user revoked at Kick or the refresh token died: nothing left to keep (Developer Agreement §5.2).
        console.log("relay: refresh rejected with invalid_grant; channel marked for reconnect");
        this.ctx.storage.sql.exec("DELETE FROM tokens");
        this.markNeedsReconnect([], Date.now() + DELETE_GRACE_MS);
        throw new NeedsReconnect();
      }
      throw error;
    }
  }

  private async storeTokens(cipher: TokenCipher, tokens: TokenSet): Promise<void> {
    const [accessEnc, refreshEnc] = await Promise.all([cipher.encrypt(tokens.access_token), cipher.encrypt(tokens.refresh_token)]);
    this.ctx.storage.sql.exec(
      `INSERT INTO tokens(id, access_enc, refresh_enc, expires_at, scope) VALUES (1, ?, ?, ?, ?)
       ON CONFLICT(id) DO UPDATE SET access_enc = excluded.access_enc, refresh_enc = excluded.refresh_enc, expires_at = excluded.expires_at, scope = excluded.scope`,
      accessEnc,
      refreshEnc,
      tokens.expires_at,
      tokens.scope,
    );
  }

  private markNeedsReconnect(missing: string[], deleteAfter: number | null): void {
    this.ctx.storage.sql.exec("UPDATE channel SET needs_reconnect = 1, missing_scopes = ?, delete_after = COALESCE(?, delete_after) WHERE id = 1", missing.join(" "), deleteAfter);
    if (deleteAfter !== null) {
      this.ctx.waitUntil(this.scheduleAlarmNoLaterThan(deleteAfter));
    }
    this.broadcast({ v: 1, type: "error", code: "needs_reconnect", detail: missing.length > 0 ? `missing scopes: ${missing.join(" ")}` : "the authorisation is no longer valid; connect again from Settings" });
  }

  private async scheduleAlarmNoLaterThan(at: number): Promise<void> {
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > at) {
      await this.ctx.storage.setAlarm(at);
    }
  }

  // ------------------------------------------------------------------------------------------------ alarm / subscriptions

  override async alarm(): Promise<void> {
    const now = Date.now();
    const row = this.channelRow();
    if (!row) {
      return;
    }
    if (row.delete_after !== null && now >= row.delete_after && !this.tokenRow()) {
      console.log("relay: grace period over, deleting channel data");
      await this.purgeAll();
      return;
    }
    this.ctx.storage.sql.exec("DELETE FROM processed_event WHERE expires_at <= ?", now);
    await this.ensureSubscriptions().catch(() => undefined);
    const next = row.delete_after !== null && row.delete_after > now ? Math.min(now + ALARM_PERIOD_MS, row.delete_after) : now + ALARM_PERIOD_MS;
    await this.ctx.storage.setAlarm(next);
  }

  /** The event names with a webhook right now, in a stable order (what the hello and `subscriptions` carry). */
  private subscribedEvents(): string[] {
    return this.ctx.storage.sql
      .exec<{ event: string }>("SELECT DISTINCT event FROM subscription ORDER BY event")
      .toArray()
      .map((r) => r.event);
  }

  /**
   * Re-subscribes whatever Kick dropped (it unsubscribes after a day of failures, PRD R2). Returns how many were added.
   * With `announce`, connected services get a `subscriptions` message whenever the set differs from before.
   */
  async ensureSubscriptions(announce = false): Promise<number> {
    if (!this.tokenRow()) {
      return 0;
    }
    let token: string;
    try {
      token = await this.accessToken();
    } catch {
      return 0;
    }
    const before = this.subscribedEvents().join(" ");
    const existing = await listSubscriptions(token);
    const sql = this.ctx.storage.sql;
    const present = new Set(existing.map((s) => `${s.event}@${s.version}`));
    // Kick's list is the truth: ids it no longer knows are dropped here too.
    sql.exec("DELETE FROM subscription");
    for (const s of existing) {
      sql.exec("INSERT OR REPLACE INTO subscription(id, event, version) VALUES (?, ?, ?)", s.id, s.event, s.version);
    }
    const missing = EVENTS.filter((e) => !present.has(`${e.name}@${e.version}`));
    let added = 0;
    if (missing.length > 0) {
      const results = await subscribeEvents(token, missing);
      for (const r of results) {
        if (r.subscription_id) {
          sql.exec("INSERT OR REPLACE INTO subscription(id, event, version) VALUES (?, ?, ?)", r.subscription_id, r.name, r.version);
          added++;
        } else {
          // Kick's reason is a short code (e.g. webhooks disabled on the app), never user content.
          console.log("relay: subscription refused", r.name, r.error ?? "unknown");
        }
      }
    }
    const after = this.subscribedEvents();
    if (announce && after.join(" ") !== before) {
      this.broadcast({ v: 1, type: "subscriptions", events: after });
    }
    return added;
  }

  // ------------------------------------------------------------------------------------------------ disconnect

  /** Deletes the subscriptions, revokes the tokens and forgets everything (PRD §5.3 `/disconnect`). */
  async disconnect(): Promise<ChannelInfo | null> {
    const channel = this.channelInfo();
    const tokenRow = this.tokenRow();
    if (tokenRow) {
      const cipher = await this.cipher();
      const ids = this.ctx.storage.sql.exec<{ id: string }>("SELECT id FROM subscription").toArray().map((r) => r.id);
      try {
        const access = await cipher.decrypt(tokenRow.access_enc);
        const refresh = await cipher.decrypt(tokenRow.refresh_enc);
        await deleteSubscriptions(access, ids);
        await revokeToken(access, "access_token");
        await revokeToken(refresh, "refresh_token");
      } catch {
        // Best effort; the local data is deleted regardless.
      }
    }
    for (const ws of this.ctx.getWebSockets()) {
      try {
        ws.close(4000, "disconnected");
      } catch {
        // already closed
      }
    }
    await this.purgeAll();
    return channel;
  }

  private async purgeAll(): Promise<void> {
    const channel = this.channelInfo();
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
    if (channel) {
      await deleteConfig(this.env, channel.user_id, channel.slug).catch(() => undefined);
    }
    this.ctx.storage.sql.exec(SCHEMA);
  }

  // ------------------------------------------------------------------------------------------------ status

  async status(): Promise<ChannelStatus> {
    const row = this.channelRow();
    const deck = this.ctx.storage.sql
      .exec<{ name: string; deckstring: string; card_class: string | null; format: string | null; updated_at: number }>("SELECT name, deckstring, card_class, format, updated_at FROM public_deck WHERE id = 1")
      .toArray()[0];
    return {
      installed: Boolean(row),
      channel: this.channelInfo(),
      needs_reconnect: Boolean(row?.needs_reconnect),
      missing_scopes: row?.missing_scopes ? row.missing_scopes.split(" ").filter(Boolean) : [],
      has_tokens: Boolean(this.tokenRow()),
      subscriptions: this.ctx.storage.sql
        .exec<{ id: string; event: string; version: number }>("SELECT id, event, version FROM subscription ORDER BY event")
        .toArray()
        .map((r) => ({ id: r.id, event: r.event, version: Number(r.version) })),
      devices: Number(this.ctx.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM device").one().n),
      sockets: this.ctx.getWebSockets().length,
      live: this.liveState(),
      delete_after: row?.delete_after ?? null,
      public_deck: deck ?? null,
    };
  }

  /** The published deck for the public page (K6); null when the page is off or nothing was published. */
  async publicDeck(): Promise<ChannelStatus["public_deck"]> {
    return (await this.status()).public_deck;
  }

  /** What `/d/<slug>` needs in one hop: the channel (for the name and the kick.com link) and the deck, or null for either. */
  async publicPage(): Promise<{ channel: ChannelInfo; deck: NonNullable<ChannelStatus["public_deck"]> } | null> {
    const status = await this.status();
    return status.channel && status.public_deck ? { channel: status.channel, deck: status.public_deck } : null;
  }

  // ------------------------------------------------------------------------------------------------ helpers

  private channelRow(): { user_id: string; slug: string; username: string; needs_reconnect: number; missing_scopes: string; delete_after: number | null } | undefined {
    return this.ctx.storage.sql
      .exec<{ user_id: string; slug: string; username: string; needs_reconnect: number; missing_scopes: string; delete_after: number | null }>(
        "SELECT user_id, slug, username, needs_reconnect, missing_scopes, delete_after FROM channel WHERE id = 1",
      )
      .toArray()[0];
  }

  private channelInfo(): ChannelInfo | null {
    const row = this.channelRow();
    return row ? { user_id: row.user_id, slug: row.slug, username: row.username } : null;
  }

  private tokenRow(): { access_enc: string; refresh_enc: string; expires_at: number; scope: string } | undefined {
    return this.ctx.storage.sql.exec<{ access_enc: string; refresh_enc: string; expires_at: number; scope: string }>("SELECT access_enc, refresh_enc, expires_at, scope FROM tokens WHERE id = 1").toArray()[0];
  }

  private liveState(): LiveState {
    const row = this.ctx.storage.sql.exec<{ is_live: number; title: string | null; started_at: string | null; ended_at: string | null }>("SELECT is_live, title, started_at, ended_at FROM live_state WHERE id = 1").toArray()[0];
    return row ? { is_live: Boolean(row.is_live), title: row.title, started_at: row.started_at, ended_at: row.ended_at } : { is_live: false, title: null, started_at: null, ended_at: null };
  }

  private metaGet(key: string): string | null {
    return this.ctx.storage.sql.exec<{ value: string }>("SELECT value FROM meta WHERE key = ?", key).toArray()[0]?.value ?? null;
  }

  private metaSet(key: string, value: string): void {
    this.ctx.storage.sql.exec("INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?)", key, value);
  }

  private cipher(): Promise<TokenCipher> {
    this.cipherPromise ??= TokenCipher.fromBase64Key(this.env.TOKEN_ENCRYPTION_KEY);
    return this.cipherPromise;
  }

  private send(ws: WebSocket, message: RelayToService): void {
    try {
      ws.send(JSON.stringify(message));
    } catch {
      // the socket is gone; the close handler cleans up
    }
  }

  private broadcast(message: RelayToService): number {
    let sent = 0;
    for (const ws of this.ctx.getWebSockets()) {
      if (ws.readyState === WebSocket.OPEN) {
        this.send(ws, message);
        sent++;
      }
    }
    return sent;
  }
}

export type { RelayErrorCode };
