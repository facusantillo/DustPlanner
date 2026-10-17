# The panel and the JSON API (dustplanner-host)

The DustPlanner service is the process the HDT plugin starts (`service\dustplanner-host.exe` next to the plugin; from
source, `dotnet run --project src\DustPlanner.Host -c Release -- --open`). It listens on loopback only
(`http://127.0.0.1:8765`, or the next free port — the real address is written to `%LocalAppData%\DustPlanner\service.json`)
and serves three things: the **panel** at `/`, the **JSON API** under `/api/`, and the **OBS overlay** at `/overlay`
([`overlay.md`](overlay.md)). It never talks to the game: it reads the exporter's files, `Power.log` and the local
database, and the only things it writes are what the CLI writes too (the database, `prefs.json`, `meta.txt`, the
per-card override file, the catalog cache).

## Pages

| Page | What it shows | What you can do |
|---|---|---|
| Home | dust, rank, today / last 7 days / this session, plans waiting to be disenchanted, a set-up checklist (export, decks and meta decks, last sync, card data, warnings) | Sync now |
| Deck | your exported HDT decks in a picker, or a pasted code; the dust gap (have / plan gives / missing); missing cards with cost; cards the game will not craft, with the reason; companions; rotation; the plan with pain and reasons, alternatives, what was not proposed and why; the warnings | What am I missing? · Plan what to dust · Save this plan · plan for another amount · plan without saved decks |
| History | plans with status (proposed / partial / confirmed), their picks and how many were done; detected collection changes with the plan they matched | — |
| Games | win rate overall and per class for a period / mode / format; recent games | Import Power.log now |
| Settings | what you play (format, classes, Wild, golden policy, guards), chat commands (prefix, language, cooldowns, aliases, custom texts — [`kick-streamerbot.md`](kick-streamerbot.md)), meta decks (`meta.txt`), cards the game refuses (search → block / allow, your overrides), card data build, language, file locations | Save preferences · Save meta decks · Block / Allow · Remove · Update card data |

The plugin's **What am I missing for the selected deck?** opens `/#/deck?code=<deckstring>`; `/#/deck?key=hdt:<id>`
works for an exported deck. The page listens to `/events` (server-sent events): after the plugin exports a new
collection the service syncs it on its own and the panel refreshes with a toast.

## API

All answers are the snake_case JSON the CLI prints with `--json`. Bodies are JSON. User errors come back as
`400 {"error": "..."}`, anything else as `500 {"error": "...", "type": "..."}`.

| Call | Body / query | Answer |
|---|---|---|
| `GET /api/service` | | name, version, url, home, pid, started_at |
| `GET /api/overview?account=` | | account(s), collection summary, export age, rank, last sync, today / week / session stats, current and last game, open plans, deck and meta-deck counts, warnings |
| `GET /api/decks?account=` | | exported decks: `key` (`hdt:<id>` / `ingame:<id>`), name, class, format, archived, card count, deckstring |
| `POST /api/need` | `{deck, account?, as_of?, locale?}` — `deck` is a code, a pasted block or a deck key | `{report, catalog_warning}` (the `need --json` report) |
| `POST /api/plan` | `{deck, account?, as_of?, locale?, need?, protect_deck_codes?, allow_no_decks?, save?}` | `{plan, catalog_warning}`; `plan.saved_plan_id` when saved |
| `POST /api/sync` | `{account?}` | `{sync, accounts: [summary…]}` — imports the inbox like `dustplanner sync` |
| `GET /api/history?account=&limit=` | | snapshots, plans with picks, events |
| `GET /api/games?account=&since=&days=&type=&format=&recent=` | | overall, by class, recent games |
| `POST /api/games/import` | `{logs?}` | files read, games parsed / inserted, warnings |
| `GET /api/prefs` · `PUT /api/prefs` | the `prefs.json` object (its `chat` block is validated too) | path, exists, prefs |
| `GET /api/meta` · `PUT /api/meta` | `{text}` | text, deck_count, warnings (a line that is not a code is refused) |
| `GET /api/cards/search?q=&limit=` | | matching collectible cards |
| `GET /api/cards/overrides` · `POST /api/cards/overrides` · `DELETE /api/cards/overrides/{dbfId}?golden=` | `{card, golden, uncraftable}` | the user's per-card craftability overrides |
| `GET /api/catalog` · `POST /api/catalog/update` | | build, locale, cards, cache info |
| `GET /api/state` · `GET /events` | | the overlay state (rank, dust, session, today, current / last game, active deck, last sync, warnings) |
| `GET /api/chat/{command}?lang=&user=` | | **plain text**, one line: the chat answer for `deck`, `rank`, `wr`, `last`, `dust` or an alias; `204` in cooldown, `404` unknown or disabled ([`kick-streamerbot.md`](kick-streamerbot.md)) |

## Status page

`/status` is the original host page: warnings, the live state as JSON and an overlay preview.
