import { SELF, env, reset } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { CHANNEL, RELAY, RelaySocket, mockKickHappyPath, outbound, pairDevice, sleep } from "./helpers";

const CONFIG_ON = { v: 1, type: "config", prefix: "!", commands: [{ name: "deck", aliases: ["mazo"] }], public_page: true, forward: { stream_status: true, redemptions: false, redemption_filter: [] } };
const DECK = { v: 1, type: "deck.publish", name: "Priest Control <b>", deckstring: "AAECAa0GAA==", card_class: "PRIEST", format: "standard" };

async function publish(deviceToken: string, publicPage = true): Promise<RelaySocket> {
  const socket = await RelaySocket.open(deviceToken);
  await socket.next(); // hello
  socket.send({ ...CONFIG_ON, public_page: publicPage });
  socket.send(DECK);
  await sleep(100);
  return socket;
}

describe("the public deck page", () => {
  beforeEach(() => outbound.install());
  afterEach(async () => {
    outbound.uninstall();
    await reset();
  });

  it("answers 404 for a channel nobody paired, and for one whose page is off", async () => {
    const unknown = await SELF.fetch(`${RELAY}/d/nobody-here`, { headers: { "accept-language": "es-AR" } });
    expect(unknown.status).toBe(404);
    expect(await unknown.text()).toContain("no está publicando su mazo");
    expect((await SELF.fetch(`${RELAY}/api/d/nobody-here/deck`)).status).toBe(404);

    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await publish(paired.deviceToken, false);
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/deck`)).status).toBe(404);
    const page = await SELF.fetch(`${RELAY}/d/${CHANNEL.slug}`);
    expect(page.status).toBe(404);
    expect(await page.text()).toContain("not publishing its deck");
    socket.close();
  });

  it("serves the published deck as JSON and as a page, with the slug in either spelling", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await publish(paired.deviceToken);

    const api = await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/deck`);
    expect(api.status).toBe(200);
    expect(api.headers.get("cache-control")).toContain("max-age=15");
    const body = (await api.json()) as { channel: { slug: string; username: string }; deck: Record<string, unknown> };
    expect(body.channel).toEqual({ slug: CHANNEL.slug, username: CHANNEL.username });
    expect(body.deck).toMatchObject({ name: "Priest Control <b>", deckstring: "AAECAa0GAA==", card_class: "PRIEST", format: "standard" });
    expect(Date.parse(body.deck["updated_at"] as string)).toBeGreaterThan(Date.now() - 10_000);

    // The username's spelling (underscore) reaches the same page.
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug.replace(/-/g, "_")}/deck`)).status).toBe(200);

    const page = await SELF.fetch(`${RELAY}/d/${CHANNEL.slug}`, { headers: { "accept-language": "es-ES,es;q=0.9" } });
    expect(page.status).toBe(200);
    expect(page.headers.get("content-type")).toContain("text/html");
    const html = await page.text();
    expect(html).toContain('<html lang="es"');
    expect(html).toContain("Priest Control &lt;b&gt;"); // escaped in the markup
    expect(html).toContain("Sacerdote · Estándar");
    expect(html).toContain(`href="https://kick.com/${CHANNEL.slug}"`);
    expect(html).toContain('<code id="code">AAECAa0GAA==</code>');
    expect(html).toContain('src="/d/_/deck-page.js?v=');
    expect(html).not.toContain("hearthstonejson"); // the page itself carries no card data or URLs; the script does
    const embedded = /<script type="application\/json" id="deck-data">(.*?)<\/script>/.exec(html)![1]!;
    expect(JSON.parse(embedded)).toMatchObject({ lang: "es", deck: { deckstring: "AAECAa0GAA==" } });
    expect(embedded).not.toContain("<"); // nothing in the JSON can close the script tag

    const english = await SELF.fetch(`${RELAY}/d/${CHANNEL.slug}?lang=en`, { headers: { "accept-language": "es" } });
    expect(await english.text()).toContain("Priest · Standard");

    const script = await SELF.fetch(`${RELAY}/d/_/deck-page.js?v=test`);
    expect(script.status).toBe(200);
    expect(script.headers.get("content-type")).toContain("javascript");
    expect(await script.text()).toContain("cards.collectible.json");
    socket.close();
  });

  it("forgets the deck as soon as the page is turned off or the channel disconnects", async () => {
    mockKickHappyPath();
    const paired = await pairDevice();
    const socket = await publish(paired.deviceToken);
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/deck`)).status).toBe(200);

    socket.send({ ...CONFIG_ON, public_page: false });
    await sleep(100);
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/deck`)).status).toBe(404);

    socket.send({ ...CONFIG_ON, public_page: true });
    socket.send(DECK);
    await sleep(100);
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/deck`)).status).toBe(200);

    await SELF.fetch(`${RELAY}/disconnect`, { method: "POST", headers: { authorization: `Bearer ${paired.deviceToken}` } });
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/deck`)).status).toBe(404);
    expect(await env.CONFIG.get(`slug:${CHANNEL.slug}`)).toBeNull();
  });

  it("ignores paths that are not a slug", async () => {
    expect((await SELF.fetch(`${RELAY}/d/`)).status).toBe(404);
    expect((await SELF.fetch(`${RELAY}/d/a/b`)).status).toBe(404);
    expect((await SELF.fetch(`${RELAY}/api/d/${CHANNEL.slug}/other`)).status).toBe(404);
    expect((await SELF.fetch(`${RELAY}/d/${CHANNEL.slug}`, { method: "POST" })).status).toBe(404);
  });
});
