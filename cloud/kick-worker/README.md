# DustPlanner Kick relay (`live.dustplanner.app`)

The small cloud piece of the Kick integration ([`docs/PRD-kick.md`](../../docs/PRD-kick.md) §4–§5.3). It exists for two
reasons only: Kick requires the app's `client_secret` to exchange and renew tokens, and it delivers webhooks to **one
public URL per app**. Everything else — rank, record, deck, the texts — stays on the streamer's PC.

```
Kick ──webhooks──▶ /webhooks/kick ──filter (KV config)──▶ ChannelDO ──WebSocket──▶ dustplanner-host (127.0.0.1)
                                                             ▲ chat.send ◀─────────────────┘
streamer's browser ──▶ /connect ──▶ id.kick.com ──▶ /oauth/callback ──▶ 127.0.0.1:<port>/kick/paired?code=…
dustplanner-host ──▶ /pair/exchange ──▶ device token ──▶ /ws
```

## What it stores, and for how long

| Where | What | Until |
|---|---|---|
| `ChannelDO` (one SQLite Durable Object per channel) | Kick access + refresh tokens **encrypted with AES-GCM** (key in a wrangler secret), the webhook subscription ids, the **SHA-256 of each paired device token**, the last live state (`is_live`, title, start/end), the published deck when the public page is on, dedupe ids (24 h / 1 h) | `/disconnect`, or 24 h after the refresh token dies |
| `PendingDO` | OAuth `state` + PKCE verifier (10 min), the one-time pairing code (2 min) | consumed or expired |
| Workers KV | `config:<user_id>` (prefix, commands, forwarding flags), `slug:<slug>` → user id, Kick's public key | `/disconnect` |

It never stores chat messages, the streamer's email (the users endpoint returns it; it is discarded), or anything about
the game. Chat lines that are not a declared command are dropped in the Worker before reaching the Durable Object.
Logs carry no message content and no tokens.

## Routes

| Route | Who calls it | What it does |
|---|---|---|
| `GET /connect?pair=<sha256 hex>&port=<8765–8774>` | the panel's *Connect with Kick* button | stores the pair hash + port + PKCE verifier under a random `state`, redirects to `id.kick.com/oauth/authorize` with the five scopes |
| `GET /oauth/callback` | Kick | exchanges the code (secret + verifier), reads the user id and channel slug, enforces `ALLOWED_SLUGS` (revoking the tokens otherwise), subscribes the three webhooks, installs the channel, shows the page that sends the browser to `127.0.0.1:<port>/kick/paired?code=…` with the code visible to paste by hand |
| `POST /pair/exchange` `{code, pair_verifier}` | dustplanner-host | single use; refuses when `SHA-256(pair_verifier) ≠ pair`; issues `device_token = <user_id>.<random>` and stores its hash |
| `GET /ws` (`Authorization: Bearer <device_token>`) | dustplanner-host | WebSocket to the channel's Durable Object (hibernating); `hello` first, then the protocol of PRD §5.3 |
| `POST /webhooks/kick` | Kick | RSA PKCS#1 v1.5 / SHA-256 signature with the fetched public key (re-fetched at most once per 5 min after a failure), events older than 10 min dropped, dedupe by message id, filter by KV config, 200 whenever the signature is valid |
| `POST /disconnect` (Bearer device token) | dustplanner-host | deletes the subscriptions, revokes both tokens, wipes the Durable Object and the KV entries |
| `GET /health` | monitoring | `{ok, version, configured}` — no channel data |
| `GET /d/<slug>` | viewers | the public deck page (K6): HTML in Spanish or English (`Accept-Language`, `?lang=` forces), the deck embedded as JSON, decoded in the browser by `/d/_/deck-page.js`; 404 page when the channel is unknown or its page is off. Slug in Kick's spelling or the username's |
| `GET /api/d/<slug>/deck` | anything | `{channel{slug, username}, deck{name, deckstring, card_class, format, updated_at}}`, 15 s cache, CORS `*`; 404 `no_deck` otherwise |
| `GET /d/_/deck-page.js` | browsers | the bundled page script (`src/generated/deck-page.js.txt`, built from `client/` by `npm run build:client`; CI fails if it is stale) |

Renewal: the Durable Object refreshes the access token 5 minutes before it expires, serialised (Kick rotates the refresh
token). `chat.send`: one per second per channel; `401` → one refresh and one retry; `429` / `5xx` → an `error` frame,
no queue. A refresh that fails with `invalid_grant` purges the tokens, tells the service `needs_reconnect`, and the
channel is deleted 24 hours later unless it reconnects. A daily alarm re-subscribes whatever Kick dropped.

## Deploy (GitHub Actions, no wrangler on your machine)

`.github/workflows/worker.yml` tests on every change and deploys on a `worker-v*` tag. One-time set-up:

1. **Cloudflare API token** — *Account API tokens → Create Token → Custom → "Edit Cloudflare Workers"* template, scoped to
   the account that holds `dustplanner.app` and to that zone. That template carries Workers Scripts Edit, Workers KV
   Storage Edit and *Zone → Workers Routes → Edit*, which the custom domain `live.dustplanner.app` needs on the first
   deploy. The first deploy also creates the Worker, which needs Workers **Admin** at the product scope — the template
   grants it. Copy the token and the account id (*Workers & Pages → Overview*, right column).
2. **GitHub → Settings → Secrets and variables → Actions**, environment `cloudflare` (or repository secrets):
   - `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`
   - `KICK_CLIENT_SECRET` — from the Kick app (VERIFICACION K1); never anywhere else
   - `TOKEN_ENCRYPTION_KEY` — `openssl rand -base64 32` (PowerShell: `[Convert]::ToBase64String((1..32 | % { Get-Random -Max 256 }) -as [byte[]])`). Losing it means every channel must reconnect; changing it has the same effect.
3. **`wrangler.toml`** — fill `KICK_CLIENT_ID` and `BOT_USER_ID` (both public) once the Kick app exists. The KV namespace
   `dustplanner-kick-relay-CONFIG` already exists in the account (id in the file). `ALLOWED_SLUGS` keeps the beta private
   (PRD Q7); empty it to open up (K8). Kick spells slugs with hyphens (`tsukuru_tv` → `tsukuru-tv`); the list accepts
   either spelling.
4. **DNS** — nothing to create: `custom_domain = true` makes Cloudflare add the record for `live.dustplanner.app`. The
   apex keeps serving GitHub Pages. Make sure no `live` record exists beforehand.
5. **Kick app** — redirect `https://live.dustplanner.app/oauth/callback`; *Enable Webhooks* with
   `https://live.dustplanner.app/webhooks/kick`; **create the app's bot user** (same *Edit* page). With webhooks off,
   Kick accepts the OAuth flow but refuses every subscription: the service shows *Connected*, its card a red notice
   about chat events, and nothing arrives — turn it on and reconnect the service (it re-subscribes on every connection);
   the Logs tab then shows `relay: webhook chat.message.sent delivered` for each command. Without the bot user,
   `POST /public/v1/chat` answers `404` and the card says `send_dropped: Kick refused the message (404: …)`. Answers
   appear in chat as **@<app name>** with the bot badge.
6. Tag: `git tag worker-v0.1.0 && git push origin worker-v0.1.0`. The workflow deploys, pushes the two secrets and curls
   `/health`, which must answer `"configured": true`.

Later deploys: bump `RELAY_VERSION` in `wrangler.toml`, tag again. Rollback: `npx wrangler rollback` from a machine with
the token, or re-tag an older commit.

## Local development

```
npm install --legacy-peer-deps        # npm 10.9's peer resolver trips on vitest 4; the flag only skips that step
npm run types                         # generates worker-configuration.d.ts (runtime types, git-ignored)
cp .dev.vars.example .dev.vars        # fake values; the tests use their own
npm test                              # vitest with the Workers runtime: no network, Kick is mocked
npm run build:client                  # rebuilds src/generated/deck-page.js.txt from client/ (commit the result)
npm run typecheck
npx wrangler dev                      # local relay on http://localhost:8787 (OAuth needs the real app; webhooks need a public URL)
```

Tests cover: signature valid / invalid / stale with a generated RSA key, the once-per-window key re-fetch, idempotency,
the command filter (prefix, aliases, the bot's own messages, undeclared commands), the OAuth flow with a mocked Kick,
the closed-beta refusal with token revocation, the one-time pairing code and the wrong verifier, `hello` and the config
replication, `chat.send` with its rate limit, `401` → refresh → retry, `429` / `5xx` errors, serialised refresh,
`invalid_grant` purge, the daily re-subscription and the grace-period deletion, `/disconnect` wiping everything, the
subscriptions reported in the `hello` and announced when they change, Kick's reason on a refused message, the public
deck page (JSON and HTML, either slug spelling, off → 404, disconnect → 404) and the browser deckstring decoder against
the python-hearthstone vectors shared with the C# codec (`tools/gen_deckstring_vectors.py`).
