# Progress log

One entry per stage: what was done, what is pending, what needs the user's verification.
Stages follow `docs/PROPUESTA.md` §9 with the adjustments in §10.

## Status at a glance

| Stage | Scope | Code / tests | Needs a human (VERIFICACION) |
|---|---|---|---|
| 0 | Exporter spike, diagnostics, repo skeleton, HDT API check | done | A1–A4, B1 ✔ done by the maintainer on HDT 1.59.2 |
| 1 | Catalog, rule tables, deckstrings, collection import | done | C1 ✔ |
| 2 | `need` (missing cards, dust, rotation) | done | — |
| 3 | Exporter release packaging, CI | done | — |
| 4 | `plan` (disenchant recommender, FsCheck properties) | done | C2 ✔ C3 ✔ (values not recorded: handling stays conservative) |
| 5 | SQLite store, `sync`, `history`, plan matching | done | follow one plan in the game and `sync` |
| 6 | Power.log tracker, `games` commands | done | **C4** — 20 real games vs HDT stats |
| 7 | Read-only MCP server | done | connect Claude Desktop / Claude Code once |
| 8 | OBS overlay host | done | **C5** — check in OBS |
| 9 | Panel (web UI) + JSON API in the service, automatic sync, plugin launches the service | done | **C6** — panel from HDT on the real machine |
| 10 | One zip: plugin + bundled service, release workflow, docs | done | install the zip, open the panel from HDT |
| K0 | Kick spike: API fields confirmed, Developer Agreement reviewed, HDT active-deck event (`docs/PRD-kick.md` §11) | done | **K1** — create the Kick app and the Cloudflare account pieces (not needed before K3) |
| K1 | Active deck: `active-deck.json`, `OverlayState.ActiveDeck`, `active_deck` table, `game.deck_id`, overlay cell | done | **K2** — deck name in the overlay when a game starts |
| K2 | Chat texts: `DustPlanner.Kick` / `ChatResponder`, `GET /api/chat/{cmd}`, `chat` block in prefs + Settings, Streamer.bot guide | done | **K3** — `!deck`, `!rank`, `!wr` answered in a live stream via Streamer.bot |
| K3 | Kick relay: Cloudflare Worker (`cloud/kick-worker`), OAuth + PKCE, channel Durable Objects, signed webhooks, `worker.yml` | done | **K1** (app + secrets) then **K4** — first deploy and a real message reaching the service |
| K4 | Host connector: pairing, DPAPI device token, WebSocket with backoff, `/api/kick/*`, Settings → Kick, `kick` events | done | K5 ✔ 2026-10-18 (connected, command seen, reconnect, disconnect) |
| K5 | Native answers: `KickChatBridge` (`chat.command` → `ChatResponder` → `chat.send`), outcome on the card, Streamer.bot duplicate warning, relay reports its subscriptions, `service.log`; relay 0.2.0, build 1.2.0 | done | **K6** — `!deck` answered by the bot with Streamer.bot closed; which account posts |

198 .NET tests + 34 Worker tests (vitest), all green in the container (Linux, .NET 10 SDK, Node 22); the Windows run is the maintainer's. The maintainer
merges and tags.

---

## Stage 0 — Spike: exporter plugin, diagnostics mode, repo skeleton

**Done**

- Repo skeleton: MIT `LICENSE`, `SECURITY.md` (zero-automation / zero-memory-reading policy), `.editorconfig`,
  `.gitattributes`, `.gitignore`, `global.json` (.NET 10 SDK), `Directory.Build.props` (nullable, warnings as
  errors, analyzers), central package management, CI workflow.
- `src/DustPlanner.Contracts` (netstandard2.0): the JSON contract — `ExportMeta` (+ `ExporterCapabilities`),
  `AccountRef`/`AccountKeys`, `CollectionExport`, `DecksExport`, `RankExport`, `DiagnosticsExport`,
  `ExportSchema` constants. snake_case naming; no serializer dependency.
- `src/DustPlanner.HdtExporter.Logic` (netstandard2.0): the testable part of the exporter — content hash,
  count-array normalisation, diagnostics sample selection (premium slots 2/3, Core set), export throttle,
  history retention. *Added beyond the planned structure* so the exporter's logic can be unit-tested on .NET
  without running HDT; the plugin project is a thin adapter over it.
- `src/DustPlanner.HdtExporter` (net472): HDT plugin. `IHdtGateway` isolates every HDT/HearthMirror/HearthDb
  call with runtime capability detection; `ExportService` writes `collection.json`, `decks.json`, `rank.json`
  (+ bounded timestamped history) to `%LocalAppData%\DustPlanner\inbox\<account_key>\`, atomically; menu with
  manual export, **diagnostics export** (raw premium-type dump + Core-set presence) and folder shortcut;
  triggers: HDT's `OnCollectionChanged` and `OnModeChanged` (HUB, TOURNAMENT, COLLECTIONMANAGER, PACKOPENING).
- `tools/HdtApiCheck`: metadata-only check of the HDT API surface the exporter uses, driven by
  `required-apis.json`; `tools/fetch-hdt-refs.sh|.ps1` to populate `lib/hdt/` (CI: GitHub release v1.55.6;
  locally: `-FromInstall`).
- Tests: `DustPlanner.Contracts.Tests` (fixture + round-trip + account key/region), `DustPlanner.HdtExporter.Logic.Tests`.
- Docs: `docs/PROPUESTA.md` (+ §10 closed decisions), `docs/VERIFICACION.md`, this file.

**Pending**

- Release packaging of the plugin as a zip (stage 3).
- Multi-targeting the exporter tests is not possible on Linux (net472); the HDT adapter itself is only
  exercised by the user (see VERIFICACION A1–A3).

**Needs user verification** — `docs/VERIFICACION.md` sections A and B: install the plugin, send back
`collection.json` and `diagnostics.json`, confirm premium slot mapping and Core-set presence, run `HdtApiCheck`
against the installed HDT version.

**Environment decisions** — see `docs/PROPUESTA.md` §10.4.

---

## Stage 1 — Base data: card catalog, rule tables, deckstrings

**Done**

- `src/DustPlanner.Core` (net10.0) created with:
  - `Cards/`: `CardDef`, `CardCatalog`, `HearthstoneJsonParser` (reads `cards.collectible.json`, keeps collectible cards,
    multi-class `classes`), `HearthstoneJsonCatalogProvider` (download from `api.hearthstonejson.com`, build number taken
    from the `/v1/latest/` redirect, cache per build under `%LocalAppData%\DustPlanner\catalog\<build>\`, daily re-check,
    offline fallback to the newest cached build), `FileCatalogProvider` for tests/offline.
  - `Rules/`: `FormatRotation` (year table, `IsStandard`, `RotatingSets` = sets shared with the previous year minus
    CORE/EVENT, next-rotation date or estimate), `DustRules` (craft/disenchant per rarity and premium kind; signature and
    diamond = unknown), `PremiumSlotMap` (HDT array index → kind; 2 and 3 unknown until confirmed), `Craftability`
    (craftable / not craftable / unknown set), `GameDataFiles` (embedded `/data/*.json` with per-directory override).
  - `Deckstrings/`: own varint codec with sideboards (E.T.C., Zilliax), legacy strings without the trailing marker,
    canonical encode; `DeckBlockParser` for pasted blocks (`### name`, `#` comments) and multi-deck files (`meta.txt`).
- `/data`: `format-rotation.json` (copied from python-hearthstone 9.22.0, GRYPHON..SCARAB), `dust-rules.json`,
  `premium-slots.json`, `non-craftable.json`.
- `tools/check_format_rotation.py`: fails CI when a year's sets or rotation date differ from python-hearthstone
  `STANDARD_SETS` / `ZODIAC_ROTATION_DATES`, or when a set listed as non-craftable is craftable upstream; warns for sets
  upstream does not know yet (today: `BE`). `tools/gen_deckstring_vectors.py`: regenerates the deckstring fixtures with
  python-hearthstone's reference codec; CI fails if the committed fixture drifts.
- Tests (`DustPlanner.Core.Tests`, 42): 23 deckstring vectors (incl. the python-hearthstone suite strings, sideboards
  with two owners and x1/x2/xN sections, legacy string), 5 invalid strings, FsCheck round-trip property, rotation,
  dust, premium slots, craftability, override directory, catalog provider with a fake HTTP handler (redirect → build,
  cache hit, offline fallback, new build).

**Pending**

- Real download from `api.hearthstonejson.com` could not be exercised from the build environment (host blocked);
  verification item C1. The parser was written against the documented field names (`dbfId`, `id`, `name`, `set`,
  `rarity`, `cardClass`, `classes`, `type`, `cost`, `collectible`).
- `next_rotation.date` is null until Blizzard announces the 2027 rotation; the CLI shows the estimate with a label.

**Needs user verification** — VERIFICACION C1 (catalog download on Windows), and a glance at which `set` names
appear in your real collection that `data/non-craftable.json` does not know (the CLI will list them in stage 2).

---

## Stage 2 — Missing cards (CLI `need`) and collection import

**Done**

- `Core/Collection`: `CollectionSnapshot` (copies per `PremiumKind` through the slot map, raw HDT array kept),
  importers for the DustPlanner export (`collection.json`, schema-checked), HSReplay-style JSON
  (`{"collection": {dbfId: [n, g, ...]}, "dust": N}`, arrays of 2–8 ints) and a hand-made CSV (`dbf_id`/`card_id`/`name`
  + `normal`/`golden`/...), `InboxLocator` (lists `inbox/<account>` folders, selects by key or BattleTag, most recent
  by default — multi-account from day one).
- `Core/Planner/MissingCards`: per-card status (needed/owned/missing, sideboard copies count), craft cost of missing
  craftable copies, Core/free cards reported as *not craftable*, unknown sets reported as *unknown craftability* (never
  counted), unknown dbfIds reported, Standard-legality warning, snapshot-age warning, and the **rotation block**
  (correction 2): cards of the deck in sets that rotate next, with the next-rotation date/estimate and the craft dust at
  risk.
- `src/DustPlanner.Cli` (`dustplanner`): `need <code|file|->`, `catalog update|info`, `collection list|show`,
  `--json` everywhere, `--home` / `DUSTPLANNER_HOME`, `--as-of`, `--dust`, `--account`, `--collection`, `--locale`.
  User errors exit with code 2 and one line on stderr.
- Tests: Core 52 (importers, inbox, missing-cards scenarios incl. sideboard, Core, unknown set, unknown id, rotation,
  complete deck, Wild cards in a Standard code); CLI 5 smoke tests running the real binary against a synthetic home.

**Pending** — nothing blocking. `collection show` lists copies of *unknown kind* (HDT slots 2/3) so the user can see how
much is excluded until `premium-slots.json` is confirmed.

**Needs user verification** — VERIFICACION C1: `dustplanner catalog update` on Windows (real HearthstoneJSON download),
then `dustplanner need <your deck code>` against your real `collection.json` and compare with the in-game crafting
screen (A2.3–A2.4 cover dust and counts).

---

## Stage 3 — Exporter packaging and release workflow

**Done**

- The exporter itself (event-driven export, schema-versioned contract) landed in stage 0; this stage adds distribution:
  `tools/package-exporter.sh` / `.ps1` build the plugin in Release and produce `DustPlanner.HdtExporter-v<version>.zip`
  (`DustPlanner/` folder with the three DLLs and `INSTALL.txt`) plus a `.sha256`; `-Install` on the PowerShell script
  copies the folder straight into `%AppData%\HearthstoneDeckTracker\Plugins\`.
- `.github/workflows/release.yml`: on a `exporter-v*` tag (or manual dispatch) it fetches the HDT references, builds,
  tests, packages, uploads the artifact and publishes a GitHub release with the zip and its checksum.
- `docs/exporter-install.md`: install from zip or from source, what the plugin writes, compatibility notes, uninstall.

**Pending**

- Tag the first release once the plugin has been verified on a real HDT (VERIFICACION A1–A3). The version comes from
  `<Version>` in `Directory.Build.props` (0.1.0).

**Needs user verification** — none beyond stage 0's items; the zip produced locally (`tools\package-exporter.ps1 -Install`)
is the easiest way to run A1.

---

## Stage 4 — Disenchant recommender (CLI `plan`)

**Done**

- `Core/Planner`:
  - `UserPrefs` (`prefs.json`): format, classes played, Wild, golden policy, meta thresholds per rarity, all pain
    weights, optimizer penalties, whitelist/blacklist, excluded decks, snapshot age limit, require-known-decks.
  - `MetaSignal` from `meta.txt` (share of decks containing each card + classes of those decks). No scraping.
  - `Refunds` (`refunds.json`): per-card windows; an active window pays the **craft** value of the same premium kind.
  - `ProtectedDecks`: from the exporter's `decks.json` (archived HDT decks skipped, `excluded_decks` honoured),
    `--protect-deck` codes, and always the target deck.
  - `CandidateBuilder`: hard protections first (copies in use = max over decks incl. target, capped at the playable
    limit; meta-protected Standard legendaries **and epics**; whitelist), **surplus per premium kind always first with
    pain 0** (correction 4, with the 4-golden-commons test), then pain categories: refund 0, blacklisted 0, redundant 1,
    premium unused 3/10, Wild-only 2 (+3 legendary), Standard 25 (+50×meta share), modifiers class-not-played +5/+12 and
    rotating −3. Golden policy decides which kind is kept. Unknown-kind (slots 2/3), Core/free, unknown-set and
    unknown-value copies are excluded with a reason.
  - `DustOptimizer`: exact DP over 5-dust units minimising pain + λ·surplus + μ·copies; `Greedy` reference.
  - `DisenchantPlanner`: packs warning always first, blockers (nothing needed, stale snapshot, no saved decks unless
    `--allow-no-decks`, infeasible), main plan, up to two alternatives (without the most painful pick; regular copies
    only), notable exclusions.
- CLI `plan <deck>`: everything `need` prints plus the plan, alternatives and exclusions; `--json`; exit 1 when no plan.
- `data/examples/{prefs.json, meta.txt, refunds.json}`.
- Tests: candidate scoring (8 scenarios), optimizer (exact cases + FsCheck: feasible iff reachable, meets target,
  respects copies, never worse than greedy, equals brute force on small inputs), planner (blockers, exact shortfall,
  alternatives, FsCheck: never proposes protected copies, meets need or explains, packs warning first, alternatives
  differ and are feasible). 92 tests total.

**Pending**

- Persisting plans and matching them against later snapshots is stage 5.

**Needs user verification** — VERIFICACION C2 (signature/diamond values) and C3 (golden refund amount) still open; run
`dustplanner plan <deck>` on your real export and sanity-check the "NOT PROPOSED" block before touching anything.

---

## Stage 5 — Persistence and history (SQLite)

**Done**

- `src/DustPlanner.Store` (Microsoft.Data.Sqlite, WAL, embedded SQL migrations with a `schema_migration` table):
  tables `account`, `collection_snapshot` + `collection_entry` (copies per premium kind + raw HDT array, deduplicated by
  content hash), `deck` + `deck_card` (in-game / HDT / target / manual, archived flag, sideboard owner), `disenchant_plan`
  + `disenchant_plan_pick` (status proposed → partial → confirmed, matched copies), `collection_event` (delta per card
  and kind, disenchanted/gained, dust estimate, plan link, confidence high/medium/low), `rank_sample` (from the exporter),
  and the `game` table reserved for the Power.log tracker.
- `HistoryService.Sync`: imports a snapshot, diffs it against the previous one per card and kind, links vanished copies
  to open plans (≤ 30 days): exact count → *high*, same card → *medium*, no plan → *low* with a rules-based dust
  estimate; updates plan status.
- CLI: `sync` (inbox → database: collection, decks, rank, events), `history` (plans + changes), `plan` now stores the
  plan (`--no-save` to skip) and falls back to decks already in the database when the inbox has no `decks.json`.
- Tests: Store (migrations/WAL, snapshot dedupe + round trip, deck upsert + planner feed, plan matching with high/low
  confidence, rank import + labels) and a CLI end-to-end test: sync → plan → simulated disenchant → sync → history.
  98 tests total.

**Pending**

- `collection_event.dust_estimate` for unplanned disenchants uses the rules at detection time (refund windows from
  `refunds.json`); the real dust delta is only known from the snapshot's dust balance, shown next to it.

**Needs user verification** — follow a stored plan once in the game, export again, run `sync` and check the change is
matched with *high* confidence (the HDT read glitches Dust Utility complained about would show as *low*-confidence
events: report them if they appear without you having touched the collection).

---

## Stage 6 — Power.log tracker (match history, win rate)

**Done**

- `src/DustPlanner.LogTracker`: `PowerLogParser` (incremental, line by line: CREATE_GAME → players with account ids →
  heroes from FULL_ENTITY in PLAY → PLAYSTATE / FIRST_PLAYER / TURN → `STATE=COMPLETE`; game type, format, build and
  player names from `DebugPrintGame`; midnight rollover; `UNKNOWN HUMAN PLAYER` names learned from later lines; local
  player inferred the hslog way — a card revealed in HAND in the initial entity dump or by the first SHOW_ENTITY into the
  hand — with fallbacks: the only human vs AI, the only named player). `LogLocator` (HDT's config → game folder, dated
  session folders `Hearthstone_YYYY_MM_DD_HH_MM_SS/Power.log` and the legacy flat file), `LogConfig` (checks/completes
  only the `[Power]` section of Blizzard's `log.config`), `GameRepository` (stage-5 `game` table, keyed so re-imports are
  no-ops; win rate overall / per class / since / by game type), `LogImporter`, `PowerLogFollower` (offset tailing for
  the overlay host, switches to a new session folder when the game is relaunched).
- CLI `games import [--logs]`, `games list`, `games stats [--since|--days] [--type] [--format]`,
  `games log-config [--apply]` (report-only by default).
- Tests: three public HearthSim logs (CC0, see `tests/DustPlanner.LogTracker.Tests/Fixtures/README.md`) + a synthetic
  log with concede, midnight rollover, AI opponent tie and an unfinished game; importer/stats, follower, log.config;
  CLI end to end. 107 tests total.

**Pending / caveats**

- No real log from this machine was available while writing the parser: see VERIFICACION C4 (validate with 20 real
  games against HDT's stats). The local-player inference is the piece most likely to need a tweak.
- `games stats` counts ties and unknown results as games but not in the win rate (wins ÷ decided games).
- Battlegrounds / Mercenaries / Duels logs are parsed but not filtered specially; stats default to every game type, use
  `--type GT_RANKED` for ladder only.

---

## Stage 7 — Read-only MCP server

**Done**

- `src/DustPlanner.App`: the loading/planning logic and the JSON projections that used to live inside the CLI, now a
  library shared by the CLI, the MCP server and (next) the overlay host — one `AppServices` (catalog, collection, decks,
  prefs, meta, refunds, `NeedAsync`, `PlanAsync`) and one set of snake_case report records. The CLI output is unchanged.
- `src/DustPlanner.Mcp` (official `ModelContextProtocol` C# SDK 2.2.0, stdio): tools `get_collection_summary`,
  `get_missing_for_deck`, `propose_disenchant_plan`, `get_history`, `get_winrate`, all annotated `readOnlyHint`; the
  database is opened read-only, plans are never stored, logs go to stderr; user-facing failures are returned as tool
  errors with the message. Server instructions state the no-automation policy to the model.
- `docs/mcp.md`: Claude Desktop / Claude Code configuration, tool table, example questions.
- Tests: `tests/DustPlanner.Mcp.Tests` drives the real binary over stdio with the official client (tool list and
  annotations, collection/missing/plan round trip, error shape, win rate from a filled database) and asserts no database
  is created by the calls. 111 tests total.

**Pending / caveats**

- The catalog is cached in memory for 10 minutes per server process; after `dustplanner catalog update` restart the
  client (or wait) to see a new build.
- No HTTP transport: stdio only, by design (local, single user, nothing listening on a port).

---

## Stage 8 — OBS overlay host

**Done**

- `src/DustPlanner.Host` (ASP.NET Core minimal API, Kestrel bound to `127.0.0.1` only): `OverlayStateService` polls
  Power.log through `PowerLogFollower` (games completed after start → session record, stored in the database like
  `games import`), the exporter's `collection.json` (dust) and `rank.json` (rank, also imported into `rank_sample`)
  by modification time; publishes an immutable `OverlayState` on change. Endpoints: `/` status page with OBS
  instructions and a live preview, `/overlay` (transparent 1920×1080, SSE-driven, `scale`/`align`/`theme`/`hide`
  options), `/api/state`, `/events` (Server-Sent Events with keep-alives).
- Parser: a game in progress now exposes a tentative local player (same fallbacks as at completion) so the overlay
  never names the local player as the opponent.
- `docs/overlay.md`: run, OBS setup, options, cell semantics, C5 verification note.
- Tests: `tests/DustPlanner.Host.Tests` starts the real host on a random loopback port with a synthetic home, logs
  folder and rank export; checks the pages, the state (rank label, dust, account), the SSE stream, that games appended
  after start update session/last game/current game and land in the database, and that pre-existing games are not
  replayed into the session. Visual check done with headless Chromium (transparent PNG). 113 tests total.

**Pending / caveats**

- VERIFICACION C5: confirm in OBS on the real machine (transparency, legibility, update latency).
- Rank comes only from the exporter (decision Q8); it refreshes when HDT next exports, not the instant a game ends.
- The session resets when the host restarts; "today" does not (it is read from the database).

---

## Stage 9 — Panel: a UI for people who will never open a terminal

Decision (2026-10-15, after the uncraftable-cards round): the CLI, the MCP server and the overlay are all for
technical users; the product a streamer installs has to be **one HDT plugin, used from HDT, no terminal**. The
proposal's Q9 ("MVP in CLI, the local web comes with the overlay host") only delivered a status page. Three options
were compared — a browser panel started by hand, a native WPF view inside HDT (needs the net10 core ported to net472
and brings the plugin DLL conflicts back with every HDT update), and a panel served by the service with the plugin
starting the service and opening it — and the third one was chosen: same web UI, the plugin owns the service.

**Done**

- `DustPlanner.App`: the operations the CLI had kept to itself are now shared — `SyncInbox` (what `sync` does),
  `SavePlan`, `ListDecks` / deck keys (`hdt:<id>`, `ingame:<id>` accepted wherever a deck code is), `ImportGames` /
  `GamesOverview`, prefs and meta read-write with validation, card search and per-card craftability overrides (with
  `Craftability.ReadOverrides` / `ClearOverride`), an in-memory catalog cache for long-running hosts. The CLI calls them.
- `DustPlanner.Host`: JSON API under `/api/*` (`docs/panel.md`), the panel at `/` (plain HTML/CSS/JS embedded in the
  assembly, English and Spanish), the old status page at `/status`, port fallback (8765..8774), `service.json` with
  the real URL and pid, `--open`. **Automatic sync**: when the exporter writes a new `collection.json` the state service
  imports it (one parser per request, serialised) and publishes `last_sync`; the panel listens to `/events`.
- Plugin (`DustPlanner.HdtExporter`): `ServiceLauncher` starts `service\dustplanner-host.exe` next to the plugin with
  HDT's data folder, bound to a Windows Job Object (`KILL_ON_JOB_CLOSE`) so it ends with HDT, reuses a service already
  answering for the same folder, and reports its state in the menu. Menu: *Open DustPlanner*, *What am I missing for the
  selected deck?* (`DeckList.Instance.ActiveDeck` → HearthDb deckstring → `/#/deck?code=`), export now, diagnostics,
  export folder, service and export status. The plugin's button opens the panel. Settings: `start_service`,
  `service_port`, `service_path`.
- Shared messages no longer name CLI commands; a `meta.txt` with a bad line is refused on save and explained on load.
- Tests: panel API (overview / decks / need by key / plan save / history, automatic sync matched against a plan,
  settings round trips: prefs validation, meta validation, overrides and their effect on the plan). 124 tests.

**Pending / caveats**

- The plugin side (launcher, Job Object, active deck) compiles against the HDT references but can only be exercised
  with HDT running: VERIFICACION C6.
- The panel opens in the default browser. A window inside HDT (WebView2) was deferred until the panel has been used
  for real; the menu would then open that window instead of the browser, nothing else changes.
- Default language follows the browser (`es` → Spanish); switchable in Settings.

## Stage 10 — One zip

- `tools/package-exporter.ps1` / `.sh` publish the service self-contained for win-x64 (single file, compressed,
  ~50 MB) into `DustPlanner\service\` and zip the plugin as `DustPlanner-v<version>.zip` (`-NoService` /
  `--no-service` for plugin-only developer builds). `-Install` copies the whole folder into HDT's Plugins folder.
- `release.yml` publishes that zip on `exporter-v*` tags; `INSTALL.txt`, README, `docs/exporter-install.md`,
  `docs/overlay.md` and the new `docs/panel.md` describe the single-plugin flow.

---

## QA round 1 — 2026-10-15

Full report: [`QA-2026-10-15.md`](QA-2026-10-15.md). Run against the live HearthstoneJSON catalog (build 255026) and a
realistic generated export with real card ids. Ten findings, nine fixed in this round (two high: four whole card sets
were "unknown craftability" because python-hearthstone's craftable list is incomplete; the MCP `get_history` tool
crashed on a populated database), plus Power.log times now parsed as local time, catalog build resolved from the
HearthstoneJSON index (no redirect any more), per-class stats honour `--format`, rotation-data staleness warning,
account prefix matching and two cosmetic fixes. 115 tests.

**Windows run (same day):** the maintainer's first `dotnet test` on Windows and first real deck surfaced seven more
items (F11–F17 in the report): Fabled companion cards in deck codes (catalog now built from `cards.json`), SQLite
pooling keeping the database locked, the overlay host racing its own initialisation, a state event per poll, the
follower's date after midnight, two PowerShell script bugs and two test portability issues. All fixed; 116 tests.

**Uncraftable copies (F18):** the first real `plan` proposed golden SN1P-SN4P and Marin the Fox, and `need` counted
Ysera, Emerald Aspect as craftable; the game marks all three *Uncraftable — cannot be disenchanted or crafted*.
Craftability is now decided per copy: HearthstoneJSON's `howToEarn` / `howToEarnGolden` (read from the enUS file for
every locale) turn reward copies into *not craftable* with the unlock text as the reason, shown in `need`, `plan` and
`--json`. The list stays updatable without a release: `collection block "<name>" [--golden]` / `unblock` write
per-card overrides to `<home>/data/non-craftable.json`, merged with the shipped file; `catalog update` brings new game
texts. 119 tests.

---

## Kick — K0 — Spike and prerequisites

Stages K0–K8 follow `docs/PRD-kick.md` (Spanish, like the PROPUESTA); decisions taken during implementation are
recorded in its §11.

**Done**

- Kick's OpenAPI (`api.kick.com/swagger/doc.yaml`) was readable this time; every field the PRD had marked
  **(OpenAPI)** is now confirmed in PRD §11.2: chat `content` is capped at 500 grapheme clusters *and* 2,048 UTF-8
  bytes, `type` is required, `broadcaster_user_id` is ignored for `type: bot`; event subscriptions infer the
  broadcaster from a user token; `GET /channels` without parameters returns the token's own channel; `DELETE
  /events/subscriptions` takes `id[]`. Rate limits are not documented (only a `429`), so the PRD's one-message-per-second
  rule stands. Which account a `type: bot` message is shown under is *not* in the spec: K5's human check.
- Kick Developer Agreement (`dev.kick.com/terms-of-service`) reviewed against the design (PRD §11.3). Two
  adjustments for K3: purge tokens and schedule channel deletion when a refresh fails with `invalid_grant` (user
  revoked at Kick), and keep all Kick-derived caches under 24 h. The privacy page (K8) is a requirement, not a nicety.
- HDT: `DeckList.Instance.ActiveDeckChanged` (`event Action<Deck?>`) and `GameEvents.OnGameStart` exist in 1.55.6
  and in HearthSim's main branch; both, plus `ActionList.Add(Action)`, `Deck.Class`, `Deck.Name` and
  `HearthDb.Deckstrings.Deck.Format`, were added to `tools/HdtApiCheck/required-apis.json`. `HdtApiCheck` is green.
- Contract decision: `active-deck.json` reuses the `meta` envelope of the other exports (PRD §11.5).

**Pending / caveats**

- Needs the maintainer (VERIFICACION K1): the Kick app from a dedicated bot account, the bot's `user_id`, Cloudflare
  Workers/KV/Durable Objects, and a free `live` DNS record. None of it blocks K1–K2.
- The 1.59.2 source tag could not be fetched from this environment; the event was confirmed in 1.55.6 (binaries) and
  in the current main branch, so an intermediate removal is very unlikely but not checked.

---

## Kick — K1 — Active deck

**Done**

- `DustPlanner.Contracts.ActiveDeckExport` (`kind: active-deck`, file `active-deck.json`): the shared `meta` envelope
  plus `deck { name, deckstring, hdt_deck_id, card_class, format }`, `deck: null` when HDT has no active deck or it is
  an Arena deck. `card_class` is upper case like `game.player_class`; `format` comes from HearthDb's `FormatType`
  (`standard | wild | classic | twist`, null when unknown). New capability flag `active_deck`.
- Exporter: `IHdtGateway.GetActiveDeck()` now carries the GUID, class and format; new `SubscribeGameStart`
  (`GameEvents.OnGameStart`) and `SubscribeActiveDeckChanged` (`DeckList.Instance.ActiveDeckChanged`). The plugin exports
  the active deck on both events (`ExportActiveDeckOnly`, no memory read, no forced collection load), inside every
  `ExportAll` (menu transitions, manual) and content-hashed (`ActiveDeckExportBuilder.ContentHash`), so an unchanged
  deck never rewrites the file. No `history\` copies: the service keeps the samples.
- Store: migration `002_active_deck.sql` (`active_deck`, 30-day retention pruned on import). `ActiveDeckRepository`
  links a game to the newest sample of the same class taken between 10 min before and 60 s after its start, creating the
  `deck` row (`origin = hdt`, `external_id = <GUID>`) when `decks.json` has not brought it. The link runs when a game
  is stored (`GameRepository.Insert`, host and `games import`) and when a sample arrives after the game.
- Host: `OverlayState.ActiveDeck` from `active-deck.json` (polled by modification time like `rank.json`), imported into
  the database on change. Overlay cell **Deck** (name, class · format), `?hide=deck`. `StoredGame.DeckId`.
- Tests: contract sample + round trip + null deck + old files without the capability; builder normalisation, hash and
  throttle; repository import/dedupe/prune, window and class rules, late export linking, newest sample wins; host state
  and end-to-end link of a tracked game. 146 tests.

**Pending / caveats**

- Which account appears as a `type: bot` sender (K5) and whether HDT's `ActiveDeckChanged` fires on the deck
  auto-selection HDT does when a game starts (if it does, the `game_start` export is simply a no-op by hash).
- VERIFICACION K2: the deck name shows up in the overlay when a game starts, on the real machine.

---

## Kick — K2 — Chat texts (the local path)

**Done**

- New library `DustPlanner.Kick` (net10): `ChatResponder` is the one place chat texts are produced; the native connector
  (K5) will call the same code. Platform-neutral names (command, viewer, stream). `ChatContext` carries what the texts
  can mention, detached from the host; `ChatTexts` holds the built-in templates, fallbacks, class / format / result
  words and the localised rank label (`Diamante 3`, `Leyenda #1.234`); `ChatFormat` does numbers (`1.240` / `1,240`,
  `70 %` / `70%`) and the limits Kick confirmed in K0 (500 grapheme clusters **and** 2,048 UTF-8 bytes, cut with an
  ellipsis). No answer starts with the prefix. Missing data → a friendly line, never an error.
- `chat` block in `prefs.json` (`ChatPrefs` in Core, validated with the rest on `PUT /api/prefs`): prefix, language
  (`null` = the Windows UI language, read with `GetUserDefaultUILanguage` because the host runs with invariant
  globalization), per-command enabled / aliases / custom template with `{placeholders}`, global (10 s) and per-viewer
  (30 s) cooldowns. Unknown commands, duplicate aliases, multi-word aliases and long templates are refused with plain
  messages; missing commands take their defaults.
- Host: `GET /api/chat/{command}?lang=&user=` → `text/plain; charset=utf-8`; `204` in cooldown, `404` unknown or
  disabled. The responder is a singleton (cooldown clocks); prefs are read per request so a save applies at once.
  Panel: **Settings → Chat commands** (prefix, language, cooldowns, a row per command with on/off, aliases and custom
  text, the placeholder list), saved with *Save preferences*.
- `docs/kick-streamerbot.md`: command + action + *Fetch URL* + *Send Message to Channel*, the `204` rule, testing from
  the browser. `docs/panel.md` lists the endpoint.
- Version bumped for the release that carries this (1.1.0 — see K4: new functionality is a minor bump, never a patch).
- Tests: the PRD's example lines in both languages, aliases, fallbacks on empty and partial state, dust off by default,
  cooldown rules (global, per viewer, case-insensitive viewer, unknown words do not count), custom templates and the
  prefix rule, deck link only with a public page, grapheme and byte limits, number / streak / class / rank formatting,
  language fallback; prefs defaults, JSON shape, normalisation and every validation message; host endpoint statuses and
  the chat block through `PUT /api/prefs`. 175 tests.

**Pending / caveats**

- The Streamer.bot step names were checked against docs.streamer.bot (Fetch URL fields, `Kick → Chat → Send Message to
  Channel`), but the maintainer does not use Streamer.bot yet: VERIFICACION K3 is the first real run.
- `{deck_url}` / `{deck_link}` stay empty until K6 publishes the deck page.
- `ChatResponder` has no state about who is live: that arrives with K7 (stream sessions).

---

## Kick — K3 — The relay (Cloudflare Worker)

**Done**

- `cloud/kick-worker/` (TypeScript, wrangler 4, vitest 4 with `@cloudflare/vitest-plugin`). Routes of PRD §5.3:
  `/connect` (pair hash + loopback port 8765–8774 under a random state, PKCE S256, the five scopes), `/oauth/callback`
  (code exchange with the secret and the verifier, `GET /users` and `GET /channels` of the token, email discarded,
  `ALLOWED_SLUGS` gate that revokes the tokens of anyone else, three webhook subscriptions, pairing code bound to the pair
  hash, page that sends the browser to `127.0.0.1:<port>/kick/paired` with the code visible), `/pair/exchange`
  (single use, verifier hash compared in constant time, device token `<user_id>.<random>` stored as SHA-256), `/ws`,
  `/webhooks/kick`, `/disconnect`, `/health`. `/d/<slug>` answers 404 until K6.
- `PendingDO` (one, SQLite): OAuth states for 10 min, pairing codes for 2 min, both single use.
- `ChannelDO` (one per channel, SQLite, hibernating WebSockets with the ping/pong auto-responder): AES-256-GCM encrypted
  tokens, scope check → `needs_reconnect` + `missing_scopes`, `hello` / `config` (replicated to KV) / `chat.send`
  (`type: bot`, 1 per second per channel, `401` → one serialised refresh and one retry, `429` → `kick_rate_limited`,
  `5xx` → `kick_unavailable`, no queue) / `deck.publish` / `ping`. Refresh 5 min before expiry, serialised because Kick
  rotates the refresh token; `invalid_grant` purges the tokens, warns the service and schedules the channel's deletion
  24 h later (Developer Agreement §5.2, PRD §11.3). Daily alarm: re-subscribe what Kick dropped (its list is the truth),
  prune dedupe tables. `/disconnect`: delete subscriptions by id, revoke both tokens, wipe storage, KV config and slug.
- Webhooks: RSA PKCS#1 v1.5 / SHA-256 over `id.timestamp.body` with the key from `/public/v1/public-key` (cached in
  memory and KV; after a failure it is fetched again at most once per 5 min, a cold start does not count), events older
  than 10 min acknowledged and dropped, dedupe by message id in the DO (24 h) and by redemption id (1 h), filter by the
  channel's KV config *before* the DO (declared commands and aliases only, never the bot's own lines, nothing for a
  channel without config except the live status), `broadcaster.channel_slug` keeps the slug index current.
  `200` whenever the signature is valid.
- Tests (30): crypto vectors (RFC 7636 PKCE), signature valid / rotated / garbage with a generated key, the refetch
  window, OAuth end to end with a mocked Kick (the `fetchMock` of the old pool no longer exists: outbound `fetch` is
  stubbed in the shared isolate), closed beta with revocation, one-time code and wrong verifier, forged device tokens,
  hello / ping / config → KV, `chat.send` rate limit and the `401`/`429`/`5xx` paths, serialised refresh,
  `invalid_grant`, `/disconnect`, webhook filter table, stale and duplicate events, live state stored for the hello,
  redemption filter and dedupe, alarm re-subscription and grace-period deletion. `wrangler deploy --dry-run` clean.
- `.github/workflows/worker.yml`: tests on every change under `cloud/kick-worker`, deploy on `worker-v*` tags with
  `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_ACCOUNT_ID`, then `KICK_CLIENT_SECRET` and `TOKEN_ENCRYPTION_KEY` pushed as
  wrangler secrets, then `/health`. `cloud/kick-worker/README.md`: what is stored and for how long, routes, set-up.
- The KV namespace `dustplanner-kick-relay-CONFIG` was created in the account; its id is in `wrangler.toml`.

**Pending / caveats**

- `KICK_CLIENT_ID` and `BOT_USER_ID` are placeholders until the Kick app exists (VERIFICACION K1); `/health` reports
  `configured: false` and `/connect` answers 503 until then.
- Which account a `type: bot` message is shown under, and Kick's real rate limits, are still K5's human check.
- `worker-configuration.d.ts` is generated (`npm run types`) and git-ignored; npm needs `--legacy-peer-deps` because npm
  10.9's resolver trips on vitest 4's optional peers (documented in the README and the workflow).

---

## Kick — K4 — The connector in the service

**Done**

- `DustPlanner.Kick.KickConnector` (hosted service): `BeginPairing(port)` (verifier, SHA-256 → the `/connect` URL,
  10 min), `CompletePairingAsync(code)` (`POST /pair/exchange`, device token saved by `FileDeviceTokenStore` —
  `%LocalAppData%\DustPlanner\kick.json`, DPAPI for the current user on Windows, in clear with `protection: none`
  elsewhere), outbound `ClientWebSocket` with `Authorization: Bearer`, `hello` → `Connected` (or `NeedsReconnect`),
  `config` sent after every hello and after each prefs save, application pings every 30 s (the exact frame the relay
  auto-answers), reconnection with exponential backoff 1–60 s and jitter, `needs_reconnect` (hello or error frame) stops
  the loop until the streamer pairs again, `401` on `/ws` and close code 4000 forget the pairing, `DisconnectAsync`
  calls `/disconnect` and wipes the token. Events for the host: `StateChanged`, `CommandReceived`, `StreamStatusChanged`,
  `RedemptionReceived`; `SendChatAsync` / `PublishDeckAsync` for K5/K6. `RelayProtocol` parses the v1 frames leniently.
- `kick` block in `prefs.json` (`KickPrefs`: `public_page`, `post_stream_summary`, `prediction_reminder`
  `off|panel|panel+chat`, `show_redemptions`, `redemption_filter`), validated with the rest; it feeds the `config`
  message (`stream_status` always forwarded — K7 needs it — redemptions only when shown).
- Host: `GET /kick/paired?code=` (bilingual page; refuses when no pairing is pending, so a replayed link cannot pair
  the service), `GET /api/kick/status`, `POST /api/kick/connect` (returns the URL; the panel opens the tab), `POST
  /api/kick/pair {code}`, `POST /api/kick/disconnect`; `/events` carries `kick` events next to `state`. `--relay <url>`
  / `DUSTPLANNER_RELAY_URL` point the service at another relay (tests use an in-process fake). `PUT /api/prefs` resets
  the chat cooldowns and pushes the new config.
- Panel: **Settings → Kick** — status card updated live (not connected / waiting for Kick / connecting / connected as
  @slug with "since" and "live now" / connect again, last error, last command received, next attempt), *Connect with
  Kick* (opens the relay in a new tab, shows the manual-code row), *Use code*, *Disconnect* (with confirmation), and the
  opt-in features of K6/K7 (labelled as arriving later). English and Spanish.
- Tests: `FakeRelay` (Kestrel, the three routes, protocol frames, knobs to misbehave) shared by both test projects.
  Connector (12): pairing and config contents, pairing restored from disk, reconnection with a second config,
  commands / live status / unknown frames, outgoing chat and deck frames and config refresh, `needs_reconnect` in the
  hello and as an error frame (no hammering, resumes after re-pairing), `401` forgets the pairing, close 4000, disconnect,
  refusals without a pending flow / bad / foreign code, protocol parsing. Host (2): the whole panel flow over HTTP and
  SSE, manual code, `kick` prefs validation. 189 .NET tests.

**Pending / caveats**

- Commands are shown ("last command: deck") but not answered: that is K5, one PR away (`ChatResponder` already exists).
- DPAPI is exercised only on Windows; the container runs the in-clear branch. VERIFICACION K5 checks the file there.
- The service does not open the browser itself: the panel does, which keeps the service free of shell-outs from request
  handlers. If a pop-up blocker interferes, the panel says so and the manual code works.
- **Version 1.1.0.** The maintainer's rule, replacing PRD §10.6: a release that adds functionality bumps the minor
  (1.1.0 = K1–K4; the next functional PR is 1.2.0), patches are for fixes only. 1.0.1 was never published.

## Kick — K5 — Commands answered by the bot

**Done**

- `DustPlanner.Kick.KickChatBridge` (hosted service): every `chat.command` the connector raises goes through the same
  `ChatResponder` instance as `GET /api/chat/…` — same texts, same cooldown clocks — and leaves as `chat.send` with
  `reply_to_message_id`, so the bot's line is a reply to the viewer's. The shared clocks mean a Streamer.bot still
  polling cannot double an answer inside the cooldown; the panel still asks to turn it off. Outcome per command
  (`answered`, `cooldown`, `ignored`, `send_failed`) in `KickConnectionState.LastCommandOutcome`, shown on the card
  ("last command: deck · answered") and logged as `Kick: !deck → answered`. A broken `prefs.json` falls back to the
  defaults, like the local endpoint.
- Connector: the hello's `subscriptions` and the relay's new `subscriptions` message land in the state; an empty list
  while connected means Kick refused the webhooks (the app's switch is off) and the card shows a red notice instead of
  a silent *Connected* — exactly what happened on the first real connection. `GET /api/chat/…` marks
  `LastLocalChatRequestAt` while connected (published at most once a minute); the card shows a yellow notice for 15 min.
- `service.log` in `%LocalAppData%\DustPlanner` (`FileLoggerProvider`, 2 MB, one rotation to `.1`): the service the
  plugin starts runs hidden, and until now its `Kick:` lines went nowhere. Off in tests (`quiet`).
- Relay 0.2.0: hello carries `subscriptions`; `ensureSubscriptions` on connection announces changes with a
  `subscriptions` frame; one log line per webhook (`relay: webhook <type> <action>`, never the body) and one per refused
  subscription (Kick's short reason), so the Cloudflare Logs tab answers "is Kick talking to us?".
- Tests: `KickChatBridgeTests` (7: answer as a reply, shared cooldown, disabled command, missing-data line,
  subscriptions in hello and announced, old relay without the field, local-request throttling), host flow extended
  (native answer over the real DI, duplicate-bot flag through SSE), `FileLoggerTests` (2), worker: hello subscriptions
  and the announcement (34 vitest). The connector tests isolate a second pairing in its own folder: reusing the first
  pairing from disk produced a session restart that could swallow a frame pushed by the test.

**Pending / caveats**

- Which account the `type: bot` message is shown under is confirmed by VERIFICACION K6.
- `chat.send` is fire-and-forget on the service side: the relay reports `send_dropped`/`kick_rate_limited` as `error`
  frames, which land in `LastError`, not in the command's outcome.
- The Streamer.bot notice relies on `/api/chat` being called; a bot that caches texts would not be detected.

