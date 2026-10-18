/** The three small pages a streamer sees in the browser during pairing. Bilingual, no scripts beyond the redirect, no cookies. */

const STYLE = `
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #15161b; color: #f1f1ee; font-family: "Segoe UI", system-ui, sans-serif; }
  main { max-width: 520px; padding: 32px 28px; background: #1f2128; border-radius: 16px; box-shadow: 0 10px 40px rgba(0,0,0,.4); }
  h1 { font-size: 22px; margin: 0 0 12px; } p { line-height: 1.5; margin: 8px 0; color: #cfd0cc; }
  code { display: inline-block; padding: 6px 10px; background: #0f1014; border-radius: 8px; font-size: 18px; letter-spacing: .04em; user-select: all; color: #fff; }
  .muted { color: #8a8c89; font-size: 13px; } a { color: #9fc5ff; }
`;

function page(title: string, body: string, extraHead = ""): Response {
  const html = `<!doctype html><html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escapeHtml(title)}</title><style>${STYLE}</style>${extraHead}</head><body><main>${body}</main></body></html>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store", "referrer-policy": "no-referrer" } });
}

export function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c] ?? c);
}

/** Shown after a successful authorisation: redirects to the local service and leaves the code visible to paste by hand. */
export function pairedPage(code: string, port: number, slug: string): Response {
  const target = `http://127.0.0.1:${port}/kick/paired?code=${encodeURIComponent(code)}`;
  const body = `
    <h1>DustPlanner · @${escapeHtml(slug)}</h1>
    <p>Volviendo al panel de DustPlanner… / Going back to the DustPlanner panel…</p>
    <p>Si no pasa nada en unos segundos, pegá este código en <b>Settings → Kick</b> (vale 2 minutos):<br>
       If nothing happens in a few seconds, paste this code in <b>Settings → Kick</b> (valid for 2 minutes):</p>
    <p><code>${escapeHtml(code)}</code></p>
    <p class="muted"><a href="${escapeHtml(target)}">Abrir el panel / Open the panel</a></p>`;
  return page("DustPlanner · Kick", body, `<meta http-equiv="refresh" content="1;url=${escapeHtml(target)}">`);
}

export function closedBetaPage(slug: string): Response {
  const body = `
    <h1>Beta cerrada / Closed beta</h1>
    <p>El canal <b>@${escapeHtml(slug)}</b> todavía no está en la lista de la beta de DustPlanner para Kick. No se guardó nada de tu cuenta.</p>
    <p>The channel <b>@${escapeHtml(slug)}</b> is not on DustPlanner's Kick beta list yet. Nothing about your account was stored.</p>
    <p class="muted"><a href="https://dustplanner.app">dustplanner.app</a></p>`;
  const response = page("DustPlanner · beta", body);
  return new Response(response.body, { status: 403, headers: response.headers });
}

export function errorPage(status: number, message: string): Response {
  const body = `
    <h1>DustPlanner · Kick</h1>
    <p>No se pudo completar la conexión / The connection could not be completed.</p>
    <p><code>${escapeHtml(message)}</code></p>
    <p>Volvé al panel y probá <b>Conectar con Kick</b> otra vez. / Go back to the panel and try <b>Connect with Kick</b> again.</p>`;
  const response = page("DustPlanner · Kick", body);
  return new Response(response.body, { status, headers: response.headers });
}
