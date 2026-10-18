/**
 * DustPlanner Kick relay — `live.dustplanner.app` (docs/PRD-kick.md §4–§5.3).
 *
 * What it does: OAuth with Kick on behalf of a streamer, receives Kick's webhooks, forwards the chat commands the
 * streamer declared to the DustPlanner service running on their PC, and posts the answers that service produces.
 * What it never does: it never sees rank, record, collection or any chat line that is not a declared command, and it
 * never stores chat. Everything Kick-specific lives here and in the service's connector; the texts live in the service.
 */
import { isConfigured, type Env } from "./env";
import { authenticateDevice, channelStub, handleCallback, handleConnect, handlePairExchange, json } from "./oauth";
import { deleteConfig } from "./config";
import { handleDeckApi, handleDeckPage, handleDeckScript, slugFromPath } from "./public-page";
import { handleWebhook } from "./webhooks";

export { ChannelDO } from "./channel-do";
export { PendingDO } from "./pending-do";

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";
    try {
      switch (path) {
        case "/":
          return Response.redirect("https://dustplanner.app", 302);
        case "/health":
          return json({ ok: true, version: env.RELAY_VERSION, configured: isConfigured(env) });
        case "/connect":
          return request.method === "GET" ? handleConnect(request, env) : methodNotAllowed();
        case "/oauth/callback":
          return request.method === "GET" ? handleCallback(request, env) : methodNotAllowed();
        case "/pair/exchange":
          return request.method === "POST" ? handlePairExchange(request, env) : methodNotAllowed();
        case "/webhooks/kick":
          return request.method === "POST" ? handleWebhook(request, env) : methodNotAllowed();
        case "/ws":
          return handleWebSocket(request, env);
        case "/disconnect":
          return request.method === "POST" ? handleDisconnect(request, env) : methodNotAllowed();
        case "/d/_/deck-page.js":
          return handleDeckScript();
        default: {
          // The public deck page (K6): /d/<slug> for people, /api/d/<slug>/deck for anything else.
          const slug = slugFromPath(path);
          if (slug && (request.method === "GET" || request.method === "HEAD")) {
            return path.startsWith("/api/") ? handleDeckApi(request, env, slug) : handleDeckPage(request, env, slug);
          }
          return json({ error: "not_found" }, 404);
        }
      }
    } catch (error) {
      // Never leak internals; the message is enough to find the log line.
      console.log("relay: unhandled error on", path, error instanceof Error ? error.message : "error");
      return json({ error: "internal" }, 500);
    }
  },
} satisfies ExportedHandler<Env>;

async function handleWebSocket(request: Request, env: Env): Promise<Response> {
  if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return json({ error: "upgrade_required" }, 426);
  }
  const device = await authenticateDevice(request, env);
  if (!device) {
    return json({ error: "unauthorized" }, 401);
  }
  const headers = new Headers(request.headers);
  headers.delete("authorization");
  headers.set("x-device-hash", device.hash);
  return channelStub(env, device.userId).fetch(new Request(request.url, { headers }));
}

async function handleDisconnect(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return json({ error: "unauthorized" }, 401);
  }
  const channel = await channelStub(env, device.userId).disconnect();
  await deleteConfig(env, device.userId, channel?.slug ?? null).catch(() => undefined);
  return json({ ok: true });
}

function methodNotAllowed(): Response {
  return json({ error: "method_not_allowed" }, 405);
}
