import { SELF, env, reset } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { CHANNEL, KICK, RELAY, TOKENS, channelStubFor, mockKickHappyPath, outbound, pairDevice, sha256Hex } from "./helpers";

describe("health and connect", () => {
  beforeEach(() => outbound.install());
  afterEach(async () => {
    outbound.uninstall();
    await reset();
  });

  it("reports health without channel data", async () => {
    const response = await SELF.fetch(`${RELAY}/health`);
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true, version: env.RELAY_VERSION, configured: true });
  });

  it("validates the pair hash and the loopback port before redirecting to Kick with PKCE", async () => {
    expect((await SELF.fetch(`${RELAY}/connect?pair=nothex&port=8765`, { redirect: "manual" })).status).toBe(400);
    expect((await SELF.fetch(`${RELAY}/connect?pair=${"a".repeat(64)}&port=80`, { redirect: "manual" })).status).toBe(400);
    expect((await SELF.fetch(`${RELAY}/connect?pair=${"a".repeat(64)}&port=8775`, { redirect: "manual" })).status).toBe(400);

    const response = await SELF.fetch(`${RELAY}/connect?pair=${"a".repeat(64)}&port=8766`, { redirect: "manual" });
    expect(response.status).toBe(302);
    const url = new URL(response.headers.get("location")!);
    expect(url.origin + url.pathname).toBe("https://id.kick.com/oauth/authorize");
    expect(url.searchParams.get("client_id")).toBe("test-client-id");
    expect(url.searchParams.get("response_type")).toBe("code");
    expect(url.searchParams.get("redirect_uri")).toBe(`${RELAY}/oauth/callback`);
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
    expect(url.searchParams.get("code_challenge")).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(url.searchParams.get("state")).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(url.searchParams.get("scope")!.split(" ").sort()).toEqual(["channel:read", "channel:rewards:read", "chat:write", "events:subscribe", "user:read"]);
  });

  it("refuses unknown routes and wrong methods without leaking anything", async () => {
    expect((await SELF.fetch(`${RELAY}/nope`)).status).toBe(404);
    expect((await SELF.fetch(`${RELAY}/d/tsukuru_tv`)).status).toBe(404);
    expect((await SELF.fetch(`${RELAY}/connect`, { method: "POST" })).status).toBe(405);
    expect((await SELF.fetch(`${RELAY}/webhooks/kick`)).status).toBe(405);
  });
});

describe("callback and pairing", () => {
  beforeEach(() => outbound.install());
  afterEach(async () => {
    outbound.uninstall();
    await reset();
  });

  it("completes the flow: exchanges with the secret and verifier, subscribes, installs the channel, hands over a one-time code", async () => {
    mockKickHappyPath();
    const paired = await pairDevice({ port: 8767 });

    expect(paired.userId).toBe(CHANNEL.userId);
    expect(paired.slug).toBe(CHANNEL.slug);
    expect(paired.deviceToken).toMatch(new RegExp(`^${CHANNEL.userId}\\.[A-Za-z0-9_-]{43}$`));

    const tokenCall = outbound.called("POST", "/oauth/token")[0]!;
    const form = new URLSearchParams(tokenCall.body);
    expect(form.get("grant_type")).toBe("authorization_code");
    expect(form.get("client_secret")).toBe("test-client-secret");
    expect(form.get("code")).toBe("the-code");
    expect(form.get("code_verifier")).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(form.get("redirect_uri")).toBe(`${RELAY}/oauth/callback`);

    const subscribe = outbound.called("POST", "/public/v1/events/subscriptions")[0]!;
    expect(JSON.parse(subscribe.body)).toEqual({
      events: [
        { name: "chat.message.sent", version: 1 },
        { name: "livestream.status.updated", version: 1 },
        { name: "channel.reward.redemption.updated", version: 1 },
      ],
      method: "webhook",
    });

    const status = await channelStubFor(paired.userId).status();
    expect(status.installed).toBe(true);
    expect(status.has_tokens).toBe(true);
    expect(status.needs_reconnect).toBe(false);
    expect(status.subscriptions.map((s) => s.event).sort()).toEqual(["channel.reward.redemption.updated", "chat.message.sent", "livestream.status.updated"]);
    expect(status.devices).toBe(1);
    expect(await env.CONFIG.get(`slug:${CHANNEL.slug}`)).toBe(CHANNEL.userId);
    // The device token itself is never stored, only its hash.
    expect(await channelStubFor(paired.userId).hasDevice(await sha256Hex(paired.deviceToken))).toBe(true);
  });

  it("shows the paired page with the redirect to the port the flow started with and the code to paste by hand", async () => {
    mockKickHappyPath();
    const pairVerifier = "v-" + crypto.randomUUID();
    const pair = await sha256Hex(pairVerifier);
    const connect = await SELF.fetch(`${RELAY}/connect?pair=${pair}&port=8770`, { redirect: "manual" });
    const state = new URL(connect.headers.get("location")!).searchParams.get("state")!;
    const callback = await SELF.fetch(`${RELAY}/oauth/callback?code=c&state=${state}`);
    const html = await callback.text();
    expect(callback.headers.get("content-type")).toContain("text/html");
    expect(callback.headers.get("cache-control")).toBe("no-store");
    expect(html).toContain('url=http://127.0.0.1:8770/kick/paired?code=');
    expect(html).toContain("Settings → Kick");
    expect(html).not.toContain("access-1"); // nothing from Kick's token response leaks into the page
  });

  it("rejects a reused or expired state and a Kick error", async () => {
    mockKickHappyPath();
    const pair = await sha256Hex("x");
    const connect = await SELF.fetch(`${RELAY}/connect?pair=${pair}&port=8765`, { redirect: "manual" });
    const state = new URL(connect.headers.get("location")!).searchParams.get("state")!;
    expect((await SELF.fetch(`${RELAY}/oauth/callback?code=c&state=${state}`)).status).toBe(200);
    expect((await SELF.fetch(`${RELAY}/oauth/callback?code=c&state=${state}`)).status).toBe(400); // single use
    expect((await SELF.fetch(`${RELAY}/oauth/callback?code=c&state=unknown`)).status).toBe(400);

    const again = await SELF.fetch(`${RELAY}/connect?pair=${pair}&port=8765`, { redirect: "manual" });
    const state2 = new URL(again.headers.get("location")!).searchParams.get("state")!;
    const denied = await SELF.fetch(`${RELAY}/oauth/callback?error=access_denied&state=${state2}`);
    expect(denied.status).toBe(400);
    expect(outbound.called("POST", "/oauth/token")).toHaveLength(1);
  });

  it("keeps channels outside the allowed list out and revokes the tokens it just obtained", async () => {
    mockKickHappyPath({ slug: "someone_else" });
    const pair = await sha256Hex("x");
    const connect = await SELF.fetch(`${RELAY}/connect?pair=${pair}&port=8765`, { redirect: "manual" });
    const state = new URL(connect.headers.get("location")!).searchParams.get("state")!;
    const callback = await SELF.fetch(`${RELAY}/oauth/callback?code=c&state=${state}`);

    expect(callback.status).toBe(403);
    expect(await callback.text()).toContain("@someone_else");
    const revokes = outbound.called("POST", "/oauth/revoke").map((c) => new URL(c.url).searchParams.get("token_hint_type")).sort();
    expect(revokes).toEqual(["access_token", "refresh_token"]);
    expect(outbound.called("POST", "/public/v1/events/subscriptions")).toHaveLength(0);
    expect((await channelStubFor(CHANNEL.userId).status()).installed).toBe(false);
  });

  it("burns the pairing code on a wrong verifier and never serves it twice", async () => {
    mockKickHappyPath();
    const pairVerifier = "v-" + crypto.randomUUID();
    const pair = await sha256Hex(pairVerifier);
    const connect = await SELF.fetch(`${RELAY}/connect?pair=${pair}&port=8765`, { redirect: "manual" });
    const state = new URL(connect.headers.get("location")!).searchParams.get("state")!;
    const html = await (await SELF.fetch(`${RELAY}/oauth/callback?code=c&state=${state}`)).text();
    const code = /<code>([^<]+)<\/code>/.exec(html)![1]!;

    const wrong = await SELF.fetch(`${RELAY}/pair/exchange`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ code, pair_verifier: "someone-elses-verifier" }) });
    expect(wrong.status).toBe(403);
    const right = await SELF.fetch(`${RELAY}/pair/exchange`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ code, pair_verifier: pairVerifier }) });
    expect(right.status).toBe(404); // consumed by the failed attempt

    expect((await SELF.fetch(`${RELAY}/pair/exchange`, { method: "POST", body: "not json" })).status).toBe(400);
    expect((await SELF.fetch(`${RELAY}/pair/exchange`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ code: "nope", pair_verifier: "x" }) })).status).toBe(404);
  });

  it("a second device for the same channel gets its own token; a device token from another channel id is refused", async () => {
    mockKickHappyPath();
    const first = await pairDevice();
    const second = await pairDevice();
    expect(first.deviceToken).not.toBe(second.deviceToken);
    expect((await channelStubFor(first.userId).status()).devices).toBe(2);

    const forged = `99999.${first.deviceToken.split(".")[1]}`;
    const ws = await SELF.fetch(`${RELAY}/ws`, { headers: { upgrade: "websocket", authorization: `Bearer ${forged}` } });
    expect(ws.status).toBe(401);
    expect((await SELF.fetch(`${RELAY}/ws`, { headers: { upgrade: "websocket", authorization: "Bearer garbage" } })).status).toBe(401);
    expect((await SELF.fetch(`${RELAY}/ws`, { headers: { authorization: `Bearer ${first.deviceToken}` } })).status).toBe(426);
  });

  it("survives a subscription failure at the callback: the channel is installed and the alarm will retry", async () => {
    mockKickHappyPath({ subscriptionErrors: true });
    const paired = await pairDevice();
    const status = await channelStubFor(paired.userId).status();
    expect(status.installed).toBe(true);
    expect(status.subscriptions).toHaveLength(0);
    expect(TOKENS.access_token).toBe("access-1");
    expect(KICK.api).toBe("https://api.kick.com");
  });
});
