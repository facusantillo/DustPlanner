import { env, reset, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { CHANNEL, KICK, channelStubFor, mockKickHappyPath, outbound, pairDevice } from "./helpers";

describe("daily alarm", () => {
  beforeEach(() => outbound.install());
  afterEach(async () => {
    outbound.uninstall();
    await reset();
  });

  it("re-subscribes the events Kick dropped and keeps the ones it still has", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const stub = channelStubFor(paired.userId);
    // Kick only remembers the chat subscription now.
    outbound.json("GET", KICK.api, "/public/v1/events/subscriptions", 200, { data: [{ id: "sub-chat-kept", event: "chat.message.sent", version: 1, method: "webhook" }] });
    outbound.calls.length = 0;

    expect(await runDurableObjectAlarm(stub)).toBe(true);

    const posted = outbound.called("POST", "/public/v1/events/subscriptions");
    expect(posted).toHaveLength(1);
    expect(JSON.parse(posted[0]!.body).events.map((e: { name: string }) => e.name)).toEqual(["livestream.status.updated", "channel.reward.redemption.updated"]);
    const status = await stub.status();
    expect(status.subscriptions.map((s: { id: string }) => s.id).sort()).toEqual(["sub-channel.reward.redemption.updated-1", "sub-chat-kept", "sub-livestream.status.updated-0"]);
    // The alarm re-arms itself.
    const next = await runInDurableObject(stub, (_i, state) => state.storage.getAlarm());
    expect(next).toBeGreaterThan(Date.now());
  });

  it("deletes a channel whose refresh token died once the grace period is over", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const stub = channelStubFor(paired.userId);
    await runInDurableObject(stub, (_i, state) => {
      state.storage.sql.exec("DELETE FROM tokens");
      state.storage.sql.exec("UPDATE channel SET needs_reconnect = 1, delete_after = ?", Date.now() - 1000);
    });
    await env.CONFIG.put(`config:${paired.userId}`, "{}");

    expect(await runDurableObjectAlarm(stub)).toBe(true);

    const status = await stub.status();
    expect(status.installed).toBe(false);
    expect(status.devices).toBe(0);
    expect(await env.CONFIG.get(`config:${paired.userId}`)).toBeNull();
    expect(await env.CONFIG.get(`slug:${CHANNEL.slug}`)).toBeNull();
    expect(await runInDurableObject(stub, (_i, state) => state.storage.getAlarm())).toBeNull();
  });

  it("keeps a channel in the grace period that still holds tokens (it reconnected in time)", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const stub = channelStubFor(paired.userId);
    await runInDurableObject(stub, (_i, state) => {
      state.storage.sql.exec("UPDATE channel SET delete_after = ?", Date.now() - 1000);
    });
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect((await stub.status()).installed).toBe(true);
  });
});
