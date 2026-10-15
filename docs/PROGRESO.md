# Progress log

One entry per stage: what was done, what is pending, what needs the user's verification.
Stages follow `docs/PROPUESTA.md` §9 with the adjustments in §10.

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
