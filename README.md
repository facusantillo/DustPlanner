# DustPlanner

A read-only dust planner for Hearthstone: tell it which deck you want to build, and it tells you which
cards you are missing, how much Arcane Dust you need, and which cards in your collection you could
disenchant to get there with the least regret.

> **Status: all eight stages built; awaiting validation on a real machine** (see [`docs/VERIFICACION.md`](docs/VERIFICACION.md)
> for what still needs a human with the game open). Progress log: [`docs/PROGRESO.md`](docs/PROGRESO.md).

## What it does — and what it never does

- Reads your collection, decks and rank **through Hearthstone Deck Tracker's public plugin API** and writes them to JSON.
- Computes missing cards and an optimal disenchant plan that respects your decks, the current meta and your preferences.
- Reads your match history from the **`Power.log` files Hearthstone writes by itself** (the same files HDT reads).
- **Never** clicks, types, injects, or reads the game's memory on its own. You disenchant by hand, in the game.
  The full policy is in [`SECURITY.md`](SECURITY.md).

## Repository layout

| Path | Target | Role |
|---|---|---|
| `src/DustPlanner.Contracts` | netstandard2.0 | JSON contract shared by the exporter and the core |
| `src/DustPlanner.HdtExporter.Logic` | netstandard2.0 | Exporter logic without HDT dependencies (testable) |
| `src/DustPlanner.HdtExporter` | net472 | Hearthstone Deck Tracker plugin (exporter) |
| `src/DustPlanner.Core` | net10.0 | Card data, deckstrings, collection, planner, meta |
| `src/DustPlanner.Store` | net10.0 | SQLite persistence: snapshots, decks, plans, history, rank, games |
| `src/DustPlanner.App` | net10.0 | Application services shared by the front ends (loading, planning, JSON projections) |
| `src/DustPlanner.Cli` | net10.0 | Command-line interface |
| `src/DustPlanner.LogTracker` | net10.0 | `Power.log` parser, importer, follower and win-rate queries |
| `src/DustPlanner.Mcp` | net10.0 | Read-only MCP server (stdio) for Claude Desktop / Claude Code |
| `src/DustPlanner.Host` | net10.0 | Loopback HTTP host: follows Power.log + exports, serves the OBS overlay |
| `tests/` | net10.0 | One test project per module |
| `tools/` | — | `HdtApiCheck`, HDT reference fetch scripts, data validation scripts |
| `data/` | — | Format rotation, dust rules, non-craftable list, example configs |
| `docs/` | — | Proposal, progress log, verification checklist |

## Building

Requires the .NET 10 SDK. The exporter compiles against assemblies shipped with HDT, which are not in the repo:

```powershell
tools\fetch-hdt-refs.ps1 -FromInstall   # Windows: copy them from your HDT installation
# or, on any OS: tools/fetch-hdt-refs.sh   (downloads HDT release v1.55.6 from GitHub)
dotnet build
dotnet test
```

## Using the CLI

```powershell
dotnet run --project src/DustPlanner.Cli -- catalog update          # fetch the card catalog (cached per build)
dotnet run --project src/DustPlanner.Cli -- collection list         # accounts exported by the HDT plugin
dotnet run --project src/DustPlanner.Cli -- need "AAECAa0GBPj..."    # missing cards for a deck code
dotnet run --project src/DustPlanner.Cli -- need deck.txt --json     # or a pasted deck block, machine-readable
dotnet run --project src/DustPlanner.Cli -- plan "AAECAa0GBPj..."    # + the least painful disenchant plan
dotnet run --project src/DustPlanner.Cli -- sync                    # store the snapshot, decks and rank; detect changes
dotnet run --project src/DustPlanner.Cli -- history                 # plans and the changes matched against them
dotnet run --project src/DustPlanner.Cli -- games import            # parse Hearthstone's Power.log files into the database
dotnet run --project src/DustPlanner.Cli -- games stats --days 7    # win rate overall and per class (also: list, log-config)
```

Typical workflow:

1. Install the exporter plugin in HDT (below), open **My Collection** once with the game running: the plugin writes
   `collection.json`, `decks.json` and `rank.json` to `%LocalAppData%\DustPlanner\inbox\<account>\`.
2. `dustplanner catalog update` (once per game patch), then `dustplanner need <deck code>` for any deck you are
   considering.
3. `dustplanner plan <deck code>` when you are short on dust. Read the *NOT PROPOSED* block as carefully as the plan.
4. Disenchant by hand in the game, if you agree. Export again (leave the collection screen) and run
   `dustplanner sync`: the change is recorded and matched against the stored plan; `dustplanner history` shows it.
5. Optional: `dustplanner games import` for match history, the MCP server to ask an AI client, the host for an OBS
   overlay.

Example (`need` against a synthetic collection):

```
DustPlanner · target deck: "(unnamed deck)" · Priest · Standard · 13 cards
Collection: Synthetic#0001 · snapshot 2026-10-14 20:10 (hdt) · catalog build synthetic

MISSING
  card                                     rarity     class    set                     copies   dust
  ---------------------------------------  ---------  -------  ----------------------  ------  -----
  Workshop Gadget (synthetic) (sideboard)  Common     Rogue    WHIZBANGS_WORKSHOP           1     40
  Lost City Scout (synthetic)              Epic       Neutral  THE_LOST_CITY                1    400
  Violet Warden (synthetic)                Rare       Mage     ESCAPEFROM_VIOLET_HOLD       2    200
  Dream Keeper (synthetic)                 Legendary  Priest   EMERALD_DREAM                1  1,600
                                                                                               -----
  Total to craft                                                                               2,240
  Dust available                                                                                 640
  Still needed                                                                                 1,600

ROTATES AT NEXT ROTATION (2027-03 (estimated)) · craft dust at risk: 2,000
  Timeways Walker (synthetic) ×2 — TIME_TRAVEL — owned
  Lost City Scout (synthetic) ×2 — THE_LOST_CITY — craft 400
  Dream Keeper (synthetic) ×1 — EMERALD_DREAM — craft 1,600
```

`plan` adds the disenchant recommendation:

```
DISENCHANT PLAN · need 1,600 · gives 1,600 (surplus 0) · pain 3 · 1 copies · 0 saved deck(s) protected
  #  card                                          kind    copies   dust  pain  why
  -  --------------------------------------------  ------  ------  -----  ----  ---------------------------------------
  1  Titan Relic (synthetic) (Legendary, Neutral)  golden       1  1,600     3  golden copy not used in any deck (Wild)

NOT PROPOSED (protected or without a known value)
  Dual Caster (synthetic) · golden ×1 · 400 dust — in use (2 in "target deck")
  ...
```

How the recommendation works, in one paragraph: copies in your saved decks (and the target deck) are never proposed;
copies above the playable limit are always free; every other copy gets a *pain* score (refund 0, redundant 1, unused
golden 3/10, Wild-only 2, Standard 25 + meta signal, +class you don't play, −rotating soon) and an exact optimizer picks
the set of copies that reaches the dust you need with the least total pain, preferring exact totals and fewer cards.
Standard legendaries and epics that show up in your `meta.txt` decks are protected. Everything configurable lives in
`prefs.json` (see `data/examples/`).

`games import` finds the `Logs` folder through HDT's configuration (or `--logs <path>`), parses every
`Hearthstone_<date>/Power.log` session and stores each game once: mode, format, both classes, opponent name, result,
who went first, turns. Hearthstone only writes `Power.log` when Blizzard's `log.config` asks for it — HDT sets that up;
`games log-config` checks it and, only with `--apply`, completes the `[Power]` section without touching anything else.

Data lives under `%LocalAppData%\DustPlanner` (override with `--home` or `DUSTPLANNER_HOME`): `inbox/` (exporter
output), `catalog/` (HearthstoneJSON cache), `data/` (optional overrides of the rule files in `/data`),
`dustplanner.db` (SQLite: snapshots, decks, plans, detected changes, rank samples, games), plus your `prefs.json`,
`meta.txt` and `refunds.json`.

## Configuration

Three optional files in the data folder (examples with every default in [`data/examples/`](data/examples/)):

- **`prefs.json`** — what you play and what you value: `format` (standard/wild), `classes_played`, `plays_wild`,
  `golden_policy` (`keep_golden` by default: goldens in use are never proposed before regular copies), the
  `meta_protection` thresholds (share of meta decks above which a Standard legendary / epic is protected), the `pain`
  score of every candidate category, optimizer weights, `whitelist` / `blacklist` of dbf ids, `excluded_decks`,
  `max_snapshot_age_days`, and `require_known_decks` (refuse to plan when no saved decks are known; `--allow-no-decks`
  overrides once).
- **`meta.txt`** — competitive deck codes you paste, one per line (`### name` lines label them). This is the *only*
  meta signal: DustPlanner never scrapes deck sites. Cards that appear in many of these decks get a lower pain score
  and, above the thresholds, full protection.
- **`refunds.json`** — active full-dust refund windows after a balance patch (`dbf_id`, `premium`, `starts_at`,
  `ends_at`, `note`), maintained by hand from the in-game *Refund* filter. Copies inside a window are the cheapest
  candidates of all; the golden refund amount is assumed equal to the golden craft cost and marked *to verify*.

Rule tables in [`data/`](data/) (`format-rotation.json`, `dust-rules.json`, `premium-slots.json`,
`non-craftable.json`) can be overridden by files of the same name under `<home>\data\`. CI checks
`format-rotation.json` against python-hearthstone's `STANDARD_SETS` so the rotation list cannot silently drift.

## Conservative by default

Any game fact the tool cannot verify keeps the item **out** of the recommendations and says why:

- HDT premium slots 2 and 3 (signature / diamond) have no confirmed dust value → *unknown kind*, never proposed.
- Cards from sets not listed in `non-craftable.json` → *unknown craftability*, listed separately in `need`.
- No HDT API exposes unopened packs → every plan opens with "open your packs first" and the count is never guessed.
- Refund amounts for golden copies → used only when `refunds.json` says so, flagged *to verify*.

## Asking an AI client (MCP)

`dustplanner-mcp` is a stdio MCP server with five read-only tools — collection summary, missing cards for a deck, a
disenchant proposal, history, win rate — so Claude Desktop or Claude Code can answer *"what am I missing for this deck
and what would you disenchant?"* from your own data. It never stores a plan, never touches the game. Setup and the tool
list: [`docs/mcp.md`](docs/mcp.md).

## OBS overlay

`dustplanner-host` follows `Power.log` and the exporter files and serves a transparent 1920×1080 page for an OBS
Browser Source — rank, session win rate, today's record, dust, last game — on `http://127.0.0.1:8765/overlay`
(loopback only). Setup, options and what each cell means: [`docs/overlay.md`](docs/overlay.md).

## Installing the exporter plugin

Download the latest `DustPlanner.HdtExporter-v*.zip` from GitHub Releases and copy its `DustPlanner` folder into
`%AppData%\HearthstoneDeckTracker\Plugins\`, or build it yourself with `tools\package-exporter.ps1 -Install`.
Full instructions: [`docs/exporter-install.md`](docs/exporter-install.md).

## Known limitations

- Windows only in practice: the exporter is an HDT plugin (.NET Framework 4.7.2) and the log/exports live in Windows
  folders. The CLI, MCP server and host build and test on Linux as well (CI does), but have nothing to read there.
- The collection comes from HDT; if HDT's reading of the game is stale or incomplete, so is the plan. `sync` reports
  low-confidence changes it cannot attribute to a plan, which is how such glitches show up.
- Rank and dust refresh when HDT exports (menu transitions), not the instant a game ends.
- The Power.log parser was validated against public HearthSim test logs, not yet against 20 real games on a user's
  machine ([`docs/VERIFICACION.md`](docs/VERIFICACION.md) C4); the OBS overlay was checked with headless Chromium, not
  OBS itself (C5).
- Firestone and other trackers are out of scope; HSReplay collection JSON and CSV exports are accepted as input.

## License and disclaimer

MIT — see [`LICENSE`](LICENSE).

Not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard
Entertainment, Inc. Card data is fetched at runtime from [HearthstoneJSON](https://hearthstonejson.com/)
and is © Blizzard Entertainment; none of it is redistributed in this repository.
