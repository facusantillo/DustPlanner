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

124 tests, all green on Windows (maintainer's machine, via the Linux VM) and in the container. No remote configured;
the maintainer publishes.

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

