import { SELF, env, reset } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { KEY_REFETCH_INTERVAL_MS, processWebhook, resetKeyCacheForTests } from "../src/webhooks";
import { CHANNEL, RelaySocket, chatMessage, makeSigner, mockKickHappyPath, mockPublicKey, outbound, pairDevice, signedWebhook, sleep, type Signer } from "./helpers";

const CONFIG = {
  v: 1,
  type: "config",
  prefix: "!",
  commands: [
    { name: "deck", aliases: ["mazo"] },
    { name: "wr", aliases: ["winrate"] },
  ],
  public_page: false,
  forward: { stream_status: true, redemptions: true, redemption_filter: ["reward-1"] },
};

async function post(request: Request): Promise<{ status: number; action: string }> {
  const response = await SELF.fetch(request);
  return { status: response.status, action: ((await response.json()) as { action: string }).action };
}

describe("webhooks", () => {
  let signer: Signer;

  beforeEach(async () => {
    outbound.install();
    resetKeyCacheForTests();
    signer = await makeSigner();
    mockPublicKey(signer);
  });
  afterEach(async () => {
    outbound.uninstall();
    await reset();
  });

  async function connectedChannel(): Promise<RelaySocket> {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await RelaySocket.open(paired.deviceToken);
    await socket.next(); // hello
    socket.send(CONFIG);
    await sleep(100);
    return socket;
  }

  it("verifies the signature with the fetched key, refusing bad or missing ones", async () => {
    const socket = await connectedChannel();
    expect(await post(await signedWebhook(signer, "chat.message.sent", chatMessage("!deck")))).toEqual({ status: 200, action: "delivered" });
    expect(await socket.next()).toMatchObject({ type: "chat.command", command: "deck", args: "", sender: { user_id: "777", username: "ana", badges: ["subscriber"] } });

    const other = await makeSigner();
    expect((await post(await signedWebhook(other, "chat.message.sent", chatMessage("!deck")))).status).toBe(401);
    const tampered = await signedWebhook(signer, "chat.message.sent", chatMessage("!deck"));
    const tamperedBody = new Request(tampered, { body: JSON.stringify({ ...chatMessage("!rank"), content: "!deck" }) });
    expect((await post(tamperedBody)).status).toBe(401);
    const noHeaders = new Request(`https://live.dustplanner.test/webhooks/kick`, { method: "POST", body: "{}" });
    expect((await post(noHeaders)).status).toBe(400);
    expect(await socket.silence(200)).toBe(true);
    socket.close();
  });

  it("re-fetches the public key at most once per window when a signature fails", async () => {
    await connectedChannel();
    expect(outbound.called("GET", "/public/v1/public-key")).toHaveLength(0);
    const t0 = Date.now();
    // First event: key fetched once (cold cache) and valid.
    expect((await processWebhook(await signedWebhook(signer, "chat.message.sent", chatMessage("!deck")), env, t0)).status).toBe(200);
    expect(outbound.called("GET", "/public/v1/public-key")).toHaveLength(1);

    // Kick rotates the key: the next event fails with the cached key, the relay fetches once and accepts it.
    const rotated = await makeSigner();
    mockPublicKey(rotated);
    expect((await processWebhook(await signedWebhook(rotated, "chat.message.sent", chatMessage("!deck")), env, t0 + 1000)).status).toBe(200);
    expect(outbound.called("GET", "/public/v1/public-key")).toHaveLength(2);

    // Garbage inside the window: refused without asking Kick again; after the window, one more fetch.
    const garbage = await makeSigner();
    expect((await processWebhook(await signedWebhook(garbage, "chat.message.sent", chatMessage("!deck")), env, t0 + 2000)).status).toBe(401);
    expect((await processWebhook(await signedWebhook(garbage, "chat.message.sent", chatMessage("!deck")), env, t0 + 3000)).status).toBe(401);
    expect(outbound.called("GET", "/public/v1/public-key")).toHaveLength(2);
    expect((await processWebhook(await signedWebhook(garbage, "chat.message.sent", chatMessage("!deck")), env, t0 + 1000 + KEY_REFETCH_INTERVAL_MS)).status).toBe(401);
    expect(outbound.called("GET", "/public/v1/public-key")).toHaveLength(3);
  });

  it("acknowledges but drops stale events and duplicates", async () => {
    const socket = await connectedChannel();
    const old = new Date(Date.now() - 11 * 60 * 1000).toISOString();
    expect(await post(await signedWebhook(signer, "chat.message.sent", chatMessage("!deck"), { timestamp: old }))).toEqual({ status: 200, action: "stale" });

    const request = await signedWebhook(signer, "chat.message.sent", chatMessage("!deck"), { messageId: "01JDUPLICATE" });
    expect(await post(request.clone())).toEqual({ status: 200, action: "delivered" });
    expect(await post(request.clone())).toEqual({ status: 200, action: "duplicate" });
    await socket.next();
    expect(await socket.silence(200)).toBe(true);
    socket.close();
  });

  it("forwards only declared commands and aliases, never the bot's own lines, and keeps the arguments", async () => {
    const socket = await connectedChannel();
    const cases: [string, string | null][] = [
      ["!deck", "deck"],
      ["!MAZO por favor", "deck"],
      ["  !winrate  ", "wr"],
      ["!rank", null], // not declared by this channel
      ["deck", null], // no prefix
      ["hola !deck", null],
      ["!deckard", null],
      ["?deck", null],
    ];
    for (const [content, expected] of cases) {
      const outcome = await post(await signedWebhook(signer, "chat.message.sent", chatMessage(content)));
      expect(outcome.action, content).toBe(expected ? "delivered" : "ignored");
      if (expected) {
        const message = await socket.next();
        expect(message["command"], content).toBe(expected);
        if (content.includes("por favor")) {
          expect(message["args"]).toBe("por favor");
        }
      }
    }
    const fromBot = chatMessage("!deck", { sender: { user_id: 900, username: "DustPlannerBot", identity: { badges: [] } } });
    expect((await post(await signedWebhook(signer, "chat.message.sent", fromBot))).action).toBe("ignored");
    const v2 = await signedWebhook(signer, "chat.message.sent", chatMessage("!deck"), { version: "2" });
    expect((await post(v2)).action).toBe("ignored");
    expect(await socket.silence(200)).toBe(true);
    socket.close();
  });

  it("drops everything but the live status for a channel without configuration, and stores that status for the hello", async () => {
    mockKickHappyPath();
    const paired = await pairDevice(); // installed, no device connected yet, no config
    expect((await post(await signedWebhook(signer, "chat.message.sent", chatMessage("!deck")))).action).toBe("ignored");
    const live = { broadcaster: { user_id: Number(CHANNEL.userId), channel_slug: CHANNEL.slug }, is_live: true, title: "Legend push", started_at: "2026-10-18T20:00:00Z", ended_at: null };
    expect((await post(await signedWebhook(signer, "livestream.status.updated", live))).action).toBe("stored");

    const socket = await RelaySocket.open(paired.deviceToken);
    const hello = await socket.next();
    expect(hello["live"]).toEqual({ is_live: true, title: "Legend push", started_at: "2026-10-18T20:00:00Z", ended_at: null });
    socket.close();
  });

  it("forwards stream status and filtered redemptions according to the channel's config, once per redemption id", async () => {
    const socket = await connectedChannel();
    const ended = { broadcaster: { user_id: Number(CHANNEL.userId), channel_slug: CHANNEL.slug }, is_live: false, title: "Legend push", started_at: "2026-10-18T20:00:00Z", ended_at: "2026-10-18T23:00:00Z" };
    expect((await post(await signedWebhook(signer, "livestream.status.updated", ended))).action).toBe("delivered");
    expect(await socket.next()).toMatchObject({ type: "stream.status", is_live: false, ended_at: "2026-10-18T23:00:00Z" });

    const redemption = (status: string, rewardId: string, id = "red-1") => ({
      id,
      user_input: "gg",
      status,
      redeemed_at: "2026-10-18T21:00:00Z",
      reward: { id: rewardId, title: "Hydrate", cost: 100, description: "" },
      redeemer: { user_id: 777, username: "ana", is_verified: false, profile_picture: "", channel_slug: "ana" },
      broadcaster: { user_id: Number(CHANNEL.userId), username: CHANNEL.username, is_verified: false, profile_picture: "", channel_slug: CHANNEL.slug },
    });
    expect((await post(await signedWebhook(signer, "channel.reward.redemption.updated", redemption("pending", "reward-1")))).action).toBe("delivered");
    expect(await socket.next()).toMatchObject({ type: "reward.redemption", id: "red-1", status: "pending", reward: { id: "reward-1", title: "Hydrate", cost: 100 }, redeemer: { username: "ana" }, user_input: "gg" });
    // pending → accepted for the same redemption is not repeated; another reward is filtered out; rejected is ignored.
    expect((await post(await signedWebhook(signer, "channel.reward.redemption.updated", redemption("accepted", "reward-1")))).action).toBe("duplicate");
    expect((await post(await signedWebhook(signer, "channel.reward.redemption.updated", redemption("pending", "reward-2", "red-2")))).action).toBe("ignored");
    expect((await post(await signedWebhook(signer, "channel.reward.redemption.updated", redemption("rejected", "reward-1", "red-3")))).action).toBe("ignored");
    expect(await socket.silence(200)).toBe(true);
    socket.close();
  });

  it("keeps the slug index current from the broadcaster block and reports no_device when nobody listens", async () => {
    const socket = await connectedChannel();
    socket.close();
    await socket.closed;
    const renamed = chatMessage("!deck", { broadcaster: { user_id: Number(CHANNEL.userId), username: CHANNEL.username, channel_slug: "tsukuru_renamed" } });
    expect((await post(await signedWebhook(signer, "chat.message.sent", renamed))).action).toBe("no_device");
    expect(await env.CONFIG.get("slug:tsukuru-renamed")).toBe(CHANNEL.userId);
  });
});
