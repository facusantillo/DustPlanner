/**
 * Test helpers: a mock for outbound `fetch` (Kick is never contacted from tests — PRD §10.4), an RSA key pair that
 * plays Kick's webhook signer, and the pairing dance done end to end against the Worker under test.
 */
import { env, SELF } from "cloudflare:test";
import { expect, vi } from "vitest";
import { toBase64 } from "../src/crypto";

export const RELAY = "https://live.dustplanner.test";

// ------------------------------------------------------------------------------------------------ outbound fetch mock

export type MockHandler = (request: Request, url: URL) => Response | Promise<Response>;

interface Route {
  method: string;
  origin: string;
  path: string | ((path: string) => boolean);
  handler: MockHandler;
  remaining: number;
}

class OutboundMock {
  readonly calls: { method: string; url: string; body: string }[] = [];
  private routes: Route[] = [];
  private readonly realFetch = globalThis.fetch;

  install(): void {
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => this.dispatch(new Request(input, init)));
  }

  uninstall(): void {
    vi.unstubAllGlobals();
    this.routes = [];
    this.calls.length = 0;
  }

  /** Registers a handler; `times` = how many calls it serves (Infinity by default). Later registrations win. */
  on(method: string, origin: string, path: string | ((path: string) => boolean), handler: MockHandler | object | ((req: Request, url: URL) => object), times = Infinity): void {
    const h: MockHandler = typeof handler === "function" ? wrapHandler(handler as MockHandler) : () => Response.json(handler);
    this.routes.unshift({ method: method.toUpperCase(), origin, path, handler: h, remaining: times });
  }

  json(method: string, origin: string, path: string, status: number, body: unknown, times = Infinity): void {
    this.on(method, origin, path, () => Response.json(body, { status }), times);
  }

  called(method: string, pathPrefix: string): { method: string; url: string; body: string }[] {
    return this.calls.filter((c) => c.method === method.toUpperCase() && new URL(c.url).pathname.startsWith(pathPrefix));
  }

  private async dispatch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const body = request.method === "GET" || request.method === "HEAD" ? "" : await request.clone().text();
    this.calls.push({ method: request.method, url: request.url, body });
    for (const route of this.routes) {
      if (route.remaining <= 0 || route.method !== request.method || route.origin !== url.origin) {
        continue;
      }
      const matches = typeof route.path === "string" ? route.path === url.pathname : route.path(url.pathname);
      if (!matches) {
        continue;
      }
      route.remaining--;
      return route.handler(request, url);
    }
    throw new Error(`unexpected outbound request in test: ${request.method} ${request.url}`);
  }
}

function wrapHandler(fn: (req: Request, url: URL) => Response | Promise<Response> | object): MockHandler {
  return async (req, url) => {
    const out = await fn(req, url);
    return out instanceof Response ? out : Response.json(out);
  };
}

export const outbound = new OutboundMock();

// ------------------------------------------------------------------------------------------------ Kick stand-ins

export const KICK = { id: "https://id.kick.com", api: "https://api.kick.com" };

export const TOKENS = {
  access_token: "access-1",
  refresh_token: "refresh-1",
  expires_in: 7200,
  scope: "user:read channel:read events:subscribe chat:write channel:rewards:read",
  token_type: "Bearer",
};

// Kick spells the slug with a hyphen; vitest.config.ts lists it with an underscore on purpose (both must match).
export const CHANNEL = { userId: "12345", slug: "tsukuru-tv", username: "Tsukuru_TV" };

/** The usual happy-path Kick: token exchange, user, channel, three subscriptions, a public key. */
export function mockKickHappyPath(options: { slug?: string; tokens?: typeof TOKENS; subscriptionErrors?: boolean } = {}): void {
  const slug = options.slug ?? CHANNEL.slug;
  outbound.json("POST", KICK.id, "/oauth/token", 200, options.tokens ?? TOKENS);
  outbound.json("POST", KICK.id, "/oauth/revoke", 200, {});
  outbound.json("GET", KICK.api, "/public/v1/users", 200, { data: [{ user_id: Number(CHANNEL.userId), name: CHANNEL.username, email: "never@read.example", profile_picture: "" }] });
  outbound.json("GET", KICK.api, "/public/v1/channels", 200, { data: [{ broadcaster_user_id: Number(CHANNEL.userId), slug, stream_title: "", stream: { is_live: false } }] });
  outbound.on("POST", KICK.api, "/public/v1/events/subscriptions", async (req) => {
    const body = (await req.json()) as { events: { name: string; version: number }[] };
    return Response.json({
      data: body.events.map((e, i) => (options.subscriptionErrors ? { name: e.name, version: e.version, error: "boom" } : { name: e.name, version: e.version, subscription_id: `sub-${e.name}-${i}` })),
    });
  });
  outbound.json("GET", KICK.api, "/public/v1/events/subscriptions", 200, { data: [] });
  outbound.json("DELETE", KICK.api, "/public/v1/events/subscriptions", 200, {});
  outbound.json("POST", KICK.api, "/public/v1/chat", 200, { data: { is_sent: true, message_id: "m-1" } });
}

// ------------------------------------------------------------------------------------------------ webhook signer

export interface Signer {
  privateKey: CryptoKey;
  pem: string;
}

export async function makeSigner(): Promise<Signer> {
  const pair = (await crypto.subtle.generateKey({ name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const spki = new Uint8Array((await crypto.subtle.exportKey("spki", pair.publicKey)) as ArrayBuffer);
  const b64 = toBase64(spki);
  const lines = b64.match(/.{1,64}/g) ?? [];
  const pem = `-----BEGIN PUBLIC KEY-----\n${lines.join("\n")}\n-----END PUBLIC KEY-----`;
  return { privateKey: pair.privateKey, pem };
}

export function mockPublicKey(signer: Signer): void {
  outbound.json("GET", KICK.api, "/public/v1/public-key", 200, { data: { public_key: signer.pem }, message: "ok" });
}

let messageCounter = 0;

export async function signedWebhook(signer: Signer, type: string, body: unknown, options: { messageId?: string; timestamp?: string; signature?: string; version?: string } = {}): Promise<Request> {
  const raw = JSON.stringify(body);
  const messageId = options.messageId ?? `01J${String(++messageCounter).padStart(10, "0")}`;
  const timestamp = options.timestamp ?? new Date().toISOString();
  const data = new TextEncoder().encode(`${messageId}.${timestamp}.${raw}`);
  const signature = options.signature ?? toBase64(new Uint8Array(await crypto.subtle.sign({ name: "RSASSA-PKCS1-v1_5" }, signer.privateKey, data)));
  return new Request(`${RELAY}/webhooks/kick`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "kick-event-message-id": messageId,
      "kick-event-subscription-id": "01JSUB",
      "kick-event-signature": signature,
      "kick-event-message-timestamp": timestamp,
      "kick-event-type": type,
      "kick-event-version": options.version ?? "1",
    },
    body: raw,
  });
}

export function chatMessage(content: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    message_id: overrides["message_id"] ?? `msg-${++messageCounter}`,
    replies_to: null,
    broadcaster: { is_anonymous: false, user_id: Number(CHANNEL.userId), username: CHANNEL.username, is_verified: false, profile_picture: "", channel_slug: CHANNEL.slug, identity: null },
    sender: { is_anonymous: false, user_id: 777, username: "ana", is_verified: false, profile_picture: "", channel_slug: "ana", identity: { username_color: "#fff", badges: [{ text: "Subscriber", type: "subscriber", count: 3 }] } },
    content,
    emotes: [],
    created_at: new Date().toISOString(),
    ...overrides,
  };
}

// ------------------------------------------------------------------------------------------------ pairing end to end

export async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}

export interface Paired {
  deviceToken: string;
  userId: string;
  slug: string;
  pairCode: string;
}

/** /connect → (Kick) → /oauth/callback → /pair/exchange, with the mocked Kick already in place. */
export async function pairDevice(options: { port?: number; pairVerifier?: string } = {}): Promise<Paired> {
  const pairVerifier = options.pairVerifier ?? "verifier-" + crypto.randomUUID();
  const pair = await sha256Hex(pairVerifier);
  const port = options.port ?? 8765;
  const connect = await SELF.fetch(`${RELAY}/connect?pair=${pair}&port=${port}`, { redirect: "manual" });
  expect(connect.status).toBe(302);
  const authorize = new URL(connect.headers.get("location")!);
  const state = authorize.searchParams.get("state")!;

  const callback = await SELF.fetch(`${RELAY}/oauth/callback?code=the-code&state=${encodeURIComponent(state)}`);
  expect(callback.status).toBe(200);
  const html = await callback.text();
  const pairCode = /<code>([^<]+)<\/code>/.exec(html)?.[1];
  expect(pairCode).toBeTruthy();

  const exchange = await SELF.fetch(`${RELAY}/pair/exchange`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ code: pairCode, pair_verifier: pairVerifier }) });
  expect(exchange.status).toBe(200);
  const result = (await exchange.json()) as { device_token: string; channel: { user_id: string; slug: string } };
  return { deviceToken: result.device_token, userId: result.channel.user_id, slug: result.channel.slug, pairCode: pairCode! };
}

// ------------------------------------------------------------------------------------------------ websocket client

export class RelaySocket {
  private readonly queue: string[] = [];
  private waiters: ((value: string) => void)[] = [];
  readonly closed: Promise<{ code: number; reason: string }>;

  private constructor(readonly ws: WebSocket) {
    ws.addEventListener("message", (event) => {
      const data = typeof event.data === "string" ? event.data : "";
      const waiter = this.waiters.shift();
      if (waiter) {
        waiter(data);
      } else {
        this.queue.push(data);
      }
    });
    this.closed = new Promise((resolve) => ws.addEventListener("close", (e) => resolve({ code: e.code, reason: e.reason })));
  }

  static async open(deviceToken: string): Promise<RelaySocket> {
    const response = await SELF.fetch(`${RELAY}/ws`, { headers: { upgrade: "websocket", authorization: `Bearer ${deviceToken}` } });
    expect(response.status).toBe(101);
    const ws = response.webSocket!;
    ws.accept();
    return new RelaySocket(ws);
  }

  static async tryOpen(deviceToken: string): Promise<number> {
    const response = await SELF.fetch(`${RELAY}/ws`, { headers: { upgrade: "websocket", authorization: `Bearer ${deviceToken}` } });
    return response.status;
  }

  send(message: object): void {
    this.ws.send(JSON.stringify(message));
  }

  sendRaw(text: string): void {
    this.ws.send(text);
  }

  async next(timeoutMs = 5000): Promise<Record<string, unknown>> {
    const queued = this.queue.shift();
    if (queued !== undefined) {
      return JSON.parse(queued) as Record<string, unknown>;
    }
    const text = await new Promise<string>((resolve, reject) => {
      const waiter = (value: string) => {
        clearTimeout(timer);
        resolve(value);
      };
      const timer = setTimeout(() => {
        // A timed-out waiter must not swallow the next message.
        this.waiters = this.waiters.filter((w) => w !== waiter);
        reject(new Error("no message within " + timeoutMs + " ms"));
      }, timeoutMs);
      this.waiters.push(waiter);
    });
    return JSON.parse(text) as Record<string, unknown>;
  }

  /** Resolves true when nothing arrives within the window: the way to assert that something was *not* forwarded. */
  async silence(ms = 300): Promise<boolean> {
    try {
      await this.next(ms);
      return false;
    } catch {
      return true;
    }
  }

  close(): void {
    this.ws.close(1000, "test done");
  }
}

export async function sleep(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

export function channelStubFor(userId: string) {
  return env.CHANNEL.get(env.CHANNEL.idFromName(userId));
}
