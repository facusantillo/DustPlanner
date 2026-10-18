import { SELF, env, reset } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { PING_FRAME, PONG_FRAME } from "../src/protocol";
import { CHANNEL, KICK, RELAY, RelaySocket, TOKENS, channelStubFor, mockKickHappyPath, outbound, pairDevice, sleep } from "./helpers";

const CONFIG = {
  v: 1,
  type: "config",
  prefix: "!",
  commands: [
    { name: "deck", aliases: ["mazo"] },
    { name: "rank", aliases: ["rango"] },
  ],
  public_page: false,
  forward: { stream_status: true, redemptions: false, redemption_filter: [] },
};

describe("websocket between the relay and the local service", () => {
  beforeEach(() => outbound.install());
  afterEach(async () => {
    outbound.uninstall();
    await reset();
  });

  it("greets a paired device with hello, answers pings and replicates the config to KV", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);

    const hello = await socket.next();
    expect(hello).toMatchObject({ v: 1, type: "hello", channel: { user_id: CHANNEL.userId, slug: CHANNEL.slug, username: CHANNEL.username }, needs_reconnect: false, missing_scopes: [], server_version: env.RELAY_VERSION });
    expect(hello["live"]).toEqual({ is_live: false, title: null, started_at: null, ended_at: null });

    socket.sendRaw(PING_FRAME);
    expect(JSON.stringify(await socket.next())).toBe(PONG_FRAME);
    socket.send({ v: 1, type: "ping" });
    expect((await socket.next())["type"]).toBe("pong");

    socket.send(CONFIG);
    await sleep(100);
    const stored = (await env.CONFIG.get(`config:${CHANNEL.userId}`, "json")) as Record<string, unknown>;
    expect(stored).toMatchObject({ prefix: "!", commands: CONFIG.commands, public_page: false, forward: CONFIG.forward });

    socket.sendRaw("not json");
    expect(await socket.next()).toMatchObject({ type: "error", code: "bad_message" });
    socket.close();
  });

  it("posts chat.send to Kick as the bot, one per second per channel", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next(); // hello

    socket.send({ v: 1, type: "chat.send", text: "Priest Control · AAECAa0G", reply_to_message_id: "msg-1" });
    await sleep(150);
    const sent = outbound.called("POST", "/public/v1/chat");
    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]!.body)).toEqual({ type: "bot", content: "Priest Control · AAECAa0G", reply_to_message_id: "msg-1" });
    expect(sent[0]!.body).not.toContain("broadcaster_user_id");

    socket.send({ v: 1, type: "chat.send", text: "too fast" });
    expect(await socket.next()).toMatchObject({ type: "error", code: "send_dropped" });
    expect(outbound.called("POST", "/public/v1/chat")).toHaveLength(1);

    await sleep(1050);
    socket.send({ v: 1, type: "chat.send", text: "a second later" });
    await sleep(150);
    expect(outbound.called("POST", "/public/v1/chat")).toHaveLength(2);
    socket.close();
  });

  it("refreshes once on 401 and retries; reports 429 and 5xx as errors without retrying", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next();

    // First send: 401 → refresh (new tokens) → retry succeeds.
    outbound.json("POST", KICK.api, "/public/v1/chat", 401, { message: "expired" }, 1);
    outbound.json("POST", KICK.id, "/oauth/token", 200, { ...TOKENS, access_token: "access-2", refresh_token: "refresh-2" }, 1);
    socket.send({ v: 1, type: "chat.send", text: "hello" });
    await sleep(200);
    const chats = outbound.called("POST", "/public/v1/chat");
    expect(chats).toHaveLength(2);
    const refresh = outbound.called("POST", "/oauth/token").at(-1)!;
    const form = new URLSearchParams(refresh.body);
    expect(form.get("grant_type")).toBe("refresh_token");
    expect(form.get("refresh_token")).toBe("refresh-1");
    expect(form.get("client_secret")).toBe("test-client-secret");
    expect(await socket.silence(200)).toBe(true);

    await sleep(1050);
    outbound.json("POST", KICK.api, "/public/v1/chat", 429, {}, 1);
    socket.send({ v: 1, type: "chat.send", text: "rate" });
    expect(await socket.next()).toMatchObject({ type: "error", code: "kick_rate_limited" });

    await sleep(1050);
    outbound.json("POST", KICK.api, "/public/v1/chat", 503, {}, 1);
    socket.send({ v: 1, type: "chat.send", text: "down" });
    expect(await socket.next()).toMatchObject({ type: "error", code: "kick_unavailable" });
    expect(outbound.called("POST", "/public/v1/chat")).toHaveLength(4); // no retries on 429 / 5xx
    socket.close();
  });

  it("marks the channel for reconnect and purges the tokens when the refresh token is dead", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next();

    outbound.json("POST", KICK.api, "/public/v1/chat", 401, {}, 1);
    outbound.json("POST", KICK.id, "/oauth/token", 400, { error: "invalid_grant", error_description: "revoked" }, 1);
    socket.send({ v: 1, type: "chat.send", text: "hello" });
    const first = await socket.next();
    const second = await socket.next();
    const codes = [first["code"], second["code"]].sort();
    expect(codes).toEqual(["needs_reconnect", "needs_reconnect"]); // the broadcast from the purge and the reply to the send

    const status = await channelStubFor(paired.userId).status();
    expect(status.has_tokens).toBe(false);
    expect(status.needs_reconnect).toBe(true);
    expect(status.delete_after).toBeGreaterThan(Date.now());
    // A new connection is told right away.
    const again = await RelaySocket.open(paired.deviceToken);
    expect(await again.next()).toMatchObject({ type: "hello", needs_reconnect: true });
    again.close();
    socket.close();
  });

  it("serialises concurrent refreshes so the rotated refresh token is never used twice", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next();

    // Make the stored token look about to expire so every call wants a refresh.
    const { runInDurableObject } = await import("cloudflare:test");
    await runInDurableObject(channelStubFor(paired.userId), async (_instance, state) => {
      state.storage.sql.exec("UPDATE tokens SET expires_at = ?", Date.now() + 60_000);
    });
    let refreshes = 0;
    outbound.on("POST", KICK.id, "/oauth/token", () => {
      refreshes++;
      return Response.json({ ...TOKENS, access_token: "access-r" + refreshes, refresh_token: "refresh-r" + refreshes });
    });
    await Promise.all([channelStubFor(paired.userId).ensureSubscriptions(), channelStubFor(paired.userId).ensureSubscriptions(), channelStubFor(paired.userId).ensureSubscriptions()]);
    expect(refreshes).toBe(1);
    socket.close();
  });

  it("disconnect deletes the subscriptions, revokes the tokens, forgets the channel and closes the socket", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next();
    socket.send(CONFIG);
    await sleep(100);
    expect(await env.CONFIG.get(`config:${paired.userId}`)).not.toBeNull();

    const response = await SELF.fetch(`${RELAY}/disconnect`, { method: "POST", headers: { authorization: `Bearer ${paired.deviceToken}` } });
    expect(response.status).toBe(200);
    const closed = await socket.closed;
    expect(closed.code).toBe(4000);

    const deleted = outbound.called("DELETE", "/public/v1/events/subscriptions")[0]!;
    expect(new URL(deleted.url).searchParams.getAll("id").sort()).toEqual(["sub-channel.reward.redemption.updated-2", "sub-chat.message.sent-0", "sub-livestream.status.updated-1"]);
    expect(outbound.called("POST", "/oauth/revoke").map((c) => new URL(c.url).searchParams.get("token_hint_type")).sort()).toEqual(["access_token", "refresh_token"]);
    expect(await env.CONFIG.get(`config:${paired.userId}`)).toBeNull();
    expect(await env.CONFIG.get(`slug:${CHANNEL.slug}`)).toBeNull();
    const status = await channelStubFor(paired.userId).status();
    expect(status.installed).toBe(false);
    expect(status.has_tokens).toBe(false);
    expect(status.devices).toBe(0);
    expect(await RelaySocket.tryOpen(paired.deviceToken)).toBe(401);
    expect((await SELF.fetch(`${RELAY}/disconnect`, { method: "POST", headers: { authorization: `Bearer ${paired.deviceToken}` } })).status).toBe(401);
  });

  it("stores the published deck only while the public page is on", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next();
    socket.send({ ...CONFIG, public_page: true });
    socket.send({ v: 1, type: "deck.publish", name: "Priest Control", deckstring: "AAECAa0G", card_class: "PRIEST", format: "standard" });
    await sleep(100);
    expect(await channelStubFor(paired.userId).publicDeck()).toMatchObject({ name: "Priest Control", deckstring: "AAECAa0G", card_class: "PRIEST", format: "standard" });
    socket.send({ ...CONFIG, public_page: false });
    await sleep(100);
    expect(await channelStubFor(paired.userId).publicDeck()).toBeNull();
    socket.close();
  });
});
