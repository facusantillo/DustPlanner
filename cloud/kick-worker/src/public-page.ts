/**
 * The public deck page (PRD §5.5): `GET /d/<slug>` and `GET /api/d/<slug>/deck`. Opt-in per channel; the relay holds
 * only name, deckstring, class, format and time, and the viewer's browser decodes the code and asks HearthstoneJSON
 * for names and costs (the script under `client/`). No cookies, no analytics. Styled like dustplanner.app.
 */
import { normalizeSlug, type Env } from "./env";
import { resolveSlug } from "./config";
import { channelStub, json } from "./oauth";
import { pickLang, renderDeckPage, renderNoDeckPage, type DeckPageData } from "./deck-html";
import deckPageScript from "./generated/deck-page.js.txt";

const PAGE_CACHE = "public, max-age=15, stale-while-revalidate=60";
const SCRIPT_CACHE = "public, max-age=86400, immutable";

/** Slug from `/d/<slug>` or `/api/d/<slug>/deck`; null when the path has another shape. */
export function slugFromPath(path: string): string | null {
  const match = /^\/(?:api\/)?d\/([A-Za-z0-9_-]{1,64})(?:\/deck)?$/.exec(path);
  return match ? normalizeSlug(match[1]!) : null;
}

async function lookup(env: Env, slug: string): Promise<DeckPageData | null> {
  const userId = await resolveSlug(env, slug);
  if (!userId) {
    return null;
  }
  const page = await channelStub(env, userId).publicPage();
  if (!page) {
    return null;
  }
  return {
    channel: { slug: page.channel.slug, username: page.channel.username },
    deck: {
      name: page.deck.name,
      deckstring: page.deck.deckstring,
      card_class: page.deck.card_class,
      format: page.deck.format,
      updated_at: new Date(page.deck.updated_at).toISOString(),
    },
  };
}

export async function handleDeckApi(request: Request, env: Env, slug: string): Promise<Response> {
  const data = await lookup(env, slug);
  if (!data) {
    return json({ error: "no_deck", detail: "this channel is not publishing its deck" }, 404);
  }
  const response = json(data);
  response.headers.set("cache-control", PAGE_CACHE);
  response.headers.set("access-control-allow-origin", "*");
  return response;
}

export function handleDeckScript(): Response {
  return new Response(deckPageScript, { headers: { "content-type": "text/javascript; charset=utf-8", "cache-control": SCRIPT_CACHE } });
}

export async function handleDeckPage(request: Request, env: Env, slug: string): Promise<Response> {
  const lang = pickLang(request);
  const data = await lookup(env, slug);
  const headers = { "content-type": "text/html; charset=utf-8", "cache-control": PAGE_CACHE, vary: "accept-language" };
  if (!data) {
    return new Response(renderNoDeckPage(lang, slug), { status: 404, headers });
  }
  return new Response(renderDeckPage(lang, data, env.RELAY_VERSION), { headers });
}
