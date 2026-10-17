# Verification checklist

Everything in this file needs **you**, a real Hearthstone account and a real HDT installation. The
code was written and tested without access to the game, so each item below is either an assumption
the code takes the conservative path on, or a step that produces data the code cannot produce itself.

Status legend: `[ ]` pending · `[x]` done · `[~]` done with caveats (see note).

---

## A. Exporter plugin (stage 0 / stage 3)

### A1. Install the plugin

1. Build it (or download a release zip once stage 3 publishes one):
   ```powershell
   tools\fetch-hdt-refs.ps1 -FromInstall         # references from YOUR HDT version
   dotnet build src\DustPlanner.HdtExporter -c Release
   ```
2. Copy the contents of `src\DustPlanner.HdtExporter\bin\Release\` — the three files
   `DustPlanner.HdtExporter.dll`, `DustPlanner.HdtExporter.Logic.dll`, `DustPlanner.Contracts.dll` —
   into a new folder `%AppData%\HearthstoneDeckTracker\Plugins\DustPlanner\`.
3. Restart HDT. Open **Options → Tracker → Plugins** and make sure *DustPlanner Exporter* is enabled.
4. A **DustPlanner** entry appears in HDT's plugin menu with: *Export collection, decks and rank now*,
   *Export diagnostics*, *Open export folder*, and a status line.

- [x] A1.1 HDT starts with the plugin enabled and shows the menu.
- [x] A1.2 `%LocalAppData%\DustPlanner\exporter.log` exists and its first line reads
      `Loaded v0.1.0.0 (HDT <your version>, HearthMirror <version>). collection event: on, mode event: on`.
      If any says `OFF`, paste the log line into an issue — it means the HDT version you run changed an API.

### A2. First export

1. Start Hearthstone, log in, open **My Collection** once, then go back to the main menu.
2. HDT refreshes its collection when you leave the collection screen; the plugin exports on that event.
   If nothing appears within a minute, use **DustPlanner → Export … now**.

- [x] A2.1 `%LocalAppData%\DustPlanner\inbox\<hi>-<lo>\collection.json` exists.
- [x] A2.2 `meta.account.battle_tag` and `meta.account.region` are yours.
- [x] A2.3 `dust` matches the dust shown in the game's crafting screen.
- [x] A2.4 Pick three cards you own (one regular ×2, one golden, one legendary ×1) and check their
      `dbfId → [n0, n1, …]` rows: index 0 = regular copies, index 1 = golden copies.
- [x] A2.5 `decks.json` exists after visiting the **Play** screen once, and lists your in-game decks
      (`origin: "ingame"`) and HDT's saved decks (`origin: "hdt"`).
- [x] A2.6 `rank.json` exists and `formats.standard.star_level` / `league_id` match your ladder standing.
- [x] A2.7 `history\` contains timestamped copies and never grows past 30 files per kind.

### A3. Diagnostics — the two open data questions (Q2, Q4)

Run **DustPlanner → Export diagnostics** with the game open and the collection loaded. It writes
`inbox\<hi>-<lo>\diagnostics.json`. Send me that file (it contains card names and counts, nothing personal
beyond your BattleTag — remove `meta.account` if you prefer).

- [x] A3.1 **Premium slots 2 and 3 (Q4).** `premium_types` lists how many cards have `premium_type` 2 and 3.
      For each card in `samples` with a non-zero value at index 2 or 3 of `raw_counts`, look it up in the game
      and note whether that copy is **Signature** or **Diamond**. Expected outcome: one mapping such as
      `2 = signature, 3 = diamond`. Until this is recorded in `data/premium-slots.json` (stage 1), copies in
      slots 2/3 have *unknown* dust value and are excluded from every recommendation.
- [x] A3.2 **Core set (Q2).** `core_set_present` tells whether CORE-set cards appear in the export at all,
      and `core_set_sample_names` lists a few. Either outcome is supported; this only decides whether the
      "missing cards" report can distinguish *Core card you have not unlocked* from *Core card not exported*.
- [x] A3.3 If `notes` contains *"No card has copies in premium slots 2 or 3"*, you own no signature/diamond
      cards and A3.1 cannot be answered from your collection. The conservative exclusion then costs nothing.

### A4. Unopened packs (correction 1)

No HDT / HearthMirror API exposes the number of unopened packs (verified on 1.55.6 binaries and 1.59.2
source: only `GetPackCards`, the pack being opened, and `GetLastOpenedBoosterId` exist). The export always
writes `unopened_packs: null` and every plan prints the "open your packs first" warning without a count.

- [x] A4.1 Confirm you are fine with the warning having no count, or tell me if you know of a source.

---

## B. HDT API compatibility (your question 5)

Every HDT API the exporter touches was checked against **HDT 1.55.6** (the latest release published on
GitHub, inspected as binaries with `tools/HdtApiCheck`) and against **HDT 1.59.2** (the current `master`
source, read by hand). All of them exist with identical signatures in both:

| API | 1.55.6 | 1.59.2 | Used for |
|---|---|---|---|
| `IPlugin` (`Name, Description, ButtonText, Author, Version, MenuItem, OnLoad, OnUnload, OnButtonPress, OnUpdate`) | ✓ | ✓ | plugin contract |
| `API.Core.Game.IsRunning` | ✓ | ✓ | skip memory reads when the game is closed |
| `API.GameEvents.OnModeChanged : ActionList<Mode>` + `Add(Action<Mode>)` | ✓ | ✓ | export on menu transitions |
| `CollectionHelpers.Hearthstone : CollectionHelper<Collection>` | ✓ | ✓ | collection access |
| `CollectionHelper<T>.GetCollection() : Task<T?>` | ✓ | ✓ | load collection |
| `CollectionHelper<T>.TryGetCollection(out T?) : bool` | ✓ | ✓ | cached collection, no memory read |
| `CollectionHelper<T>.OnCollectionChanged : event Action` | ✓ | ✓ | export trigger |
| `Collection.Cards : SortedDictionary<int,int[]>`, `.Dust`, `CollectionBase.AccountHi/Lo/BattleTag` | ✓ | ✓ | payload |
| `DeckList.Instance.Decks`, `Deck.GetSelectedDeckVersion()`, `.DeckId`, `.Sideboards`, `.IsWildDeck`, `.IsArenaDeck`, `.Archived`, `.LastEdited`, `.Class` | ✓ | ✓ | HDT saved decks |
| `HearthMirror.Reflection.Client : IReflection`, `Reflection.ClientReadTimeout(int)` | ✓ | ✓ | bounded memory reads (HDT's own mechanism) |
| `IReflection.GetFullCollection()`, `.GetDecks()`, `.GetMedalInfo()`, `.GetAccountId()`, `.GetBattleTag()` | ✓ | ✓ | raw collection, in-game decks, rank, account |
| `HearthMirror.Objects.Card.PremiumType/Count/TrialCount`, `Deck.Sideboards : Dictionary<string,List<Card>>`, `Deck.FormatType/Type`, `MedalInfo.Standard/Wild/Classic/Twist`, `MedalInfoData.*` | ✓ | ✓ | payload |
| `HearthDb.Cards.GetFromDbfId(int, bool)`, `.CardIdToDbfId`, `.All`; `Card.Set/Rarity/Class/Name` | ✓ | ✓ | card names, Core-set detection |

Not available in either version: any API for the unopened pack count (see A4).

Even so, every call goes through `IHdtGateway` (`src/DustPlanner.HdtExporter/Hdt/HdtGateway.cs`): each API
lives in its own non-inlined method and a `TypeLoadException` / `MissingMemberException` degrades that one
capability (recorded in `meta.capabilities` of every export) instead of failing the plugin. The plugin keeps
loading and the status line says which feature is off.

- [x] B1. Run the check against your own HDT and attach the report:
  ```powershell
  dotnet run --project tools\HdtApiCheck -- "$env:LOCALAPPDATA\HearthstoneDeckTracker\app-<version>" --report hdt-api-report.md
  ```
  Expected last line: **Result: all required APIs present.**
  *Done 2026-10-15 against the installed `app-1.59.2` (HDT 1.59.2.9014, HearthMirror 32.4.2.0, HearthDb 37.0.0.0):
  all 45 APIs present — report in [`hdt-api-report-1.59.2.md`](hdt-api-report-1.59.2.md). One difference surfaced:
  the installed build names its executable `HearthstoneDeckTracker.exe`, the 1.55.6 release zip
  `Hearthstone Deck Tracker.exe`; the check tool and `fetch-hdt-refs` now accept both.*

---

## C. Pending for later stages (filled in as stages land)

- [x] C1. Catalog download on your machine: `dotnet run --project src/DustPlanner.Cli -- catalog update` must print
      `Catalog build <number> (enUS): <N> collectible cards`. Then `dustplanner collection show` and
      `dustplanner need <deck code>`; compare the missing list and dust with the in-game crafting screen for one deck.
      Paste back any line starting with `Sets not known to data/non-craftable.json` — those set names need a decision.
- [x] C2. Signature / diamond dust values (craft and disenchant) — unknown; excluded until confirmed (stage 1/4).
- [x] C3. Golden-card refund amount for nerfed cards (assumed = golden craft cost) — **to verify** (stage 4).
- [ ] C4. **Validate the Power.log tracker with 20 real games** (stage 6). The parser was written without any log
      from your machine: its rules come from HearthSim's `hslog` reference parser and three public test logs from
      `hsreplay-test-data` (builds 25770, 88998 and 186852) plus a hand-written log for edge cases. What to check:
      1. `dustplanner games log-config` reports the `[Power]` section as complete (HDT sets it up; `--apply` only if
         it does not).
      2. Play (or have already played) 20 games in Ranked/Casual, then `dustplanner games import` and
         `dustplanner games list --limit 20`.
      3. Compare each row with HDT's *Stats* window: same count of games (nothing missing, nothing duplicated on a
         second `import`), same **result** (win/loss; concedes count as losses), same **class** and **opponent class**,
         same **first/second** and roughly the same start time (the session folder date + the log time, local).
      4. Edge cases worth one game each if you can: a concede, a game crossing midnight, a reconnect (close the client
         mid-game and reopen it: the game should appear once, not twice), an AI game ("vs innkeeper").
      Anything that differs → paste the `Hearthstone_<date>/Power.log` session folder name and the row; the
      inference of the *local* player (revealed hand → controller) is the most likely culprit and is isolated in
      `PowerLogParser.EndBlock`.
      Note: with HDT running, finished sessions hold `Power_old.log` (HDT renames the file when its watcher starts);
      both names are read, rotated file first. First real run (2026-10-15): 5 ranked games from 2 files, all with
      result, classes, opponent, first player and turns — the comparison with HDT's Stats window is still yours to do.
- [ ] C6. **Panel from HDT** (stage 9/10): install the zip from `tools\package-exporter.ps1 -Install` (HDT closed),
      start HDT, enable the plugin. Check: the *Service:* line in the plugin menu says *running at http://127.0.0.1:8765*
      within a minute; *Open DustPlanner* opens the panel with your account on Home; *What am I missing for the selected
      deck?* opens the Deck page already analysed; after a disenchant and leaving the Collection, Home shows a new
      *Last sync* and History marks the plan without pressing anything; closing HDT ends `dustplanner-host.exe`
      (Task Manager). Anything that differs → paste `%LocalAppData%\DustPlanner\exporter.log`.
- [ ] C5. **OBS overlay** (stage 8): run `dotnet run --project src\DustPlanner.Host -c Release`, add a Browser Source
      with `http://127.0.0.1:8765/overlay` at 1920×1080 (docs/overlay.md) and confirm: transparent background (only
      the bar is visible), legible at 1080p, the *Session* cell changes within ~5 s after a game ends, *Rank* and
      *Dust* change after HDT's next export (leave and re-enter the collection / Play screen). Checked here only with
      headless Chromium (transparent PNG, alpha 0 outside the bar), never with OBS itself.

---

## K. Kick (stages K0–K8, `docs/PRD-kick.md`)

- [ ] K1. **Kick app and Cloudflare prerequisites** (K0, needed before K3 can be tried for real; nothing in K1–K2
      depends on it). From a **dedicated bot account** (proposed name *DustPlannerBot*), with 2FA enabled:
      `kick.com/settings/developer` → create app with redirect `https://live.dustplanner.app/oauth/callback`, then
      *Edit → Enable Webhooks* with `https://live.dustplanner.app/webhooks/kick`. Note the `client_id` (public, goes in
      `wrangler.toml`) and the bot account's `user_id` (`GET /public/v1/users` with the bot's own token, or the
      developer page). The `client_secret` goes **only** into `wrangler secret put KICK_CLIENT_SECRET`, never in chat,
      files or issues. In Cloudflare (account holding `dustplanner.app`): Workers + KV + Durable Objects enabled, and
      check that no `live` DNS record exists in the zone. In GitHub: secrets `CLOUDFLARE_API_TOKEN` and
      `CLOUDFLARE_ACCOUNT_ID` for `worker.yml` (K3).
- [ ] K2. **Active deck in the overlay** (K1): with the new plugin build installed and the service running, select a
      deck in HDT and start a game. Check: `%LocalAppData%\DustPlanner\inbox\<account>\active-deck.json` appears within
      seconds (`meta.trigger` = `active_deck_changed` or `game_start`, `deck.name` is your deck, `deck.card_class` upper
      case), the overlay shows a **Deck** cell with the name, and after the game `dustplanner games list --limit 1`
      (or the panel's Games page) shows the game; in `dustplanner.db`, `SELECT deck_id FROM game ORDER BY id DESC
      LIMIT 1` is not NULL. Select an Arena deck: the file says `"deck": null` and the cell disappears. Anything that
      differs → paste `exporter.log` lines containing `active deck`.
- [ ] K3. **Chat commands through Streamer.bot** (K2): with the service running, open
      `http://127.0.0.1:8765/api/chat/deck?lang=es` in a browser and read the line; then follow
      `docs/kick-streamerbot.md` for `!deck`, `!rank` and `!wr`, type them in your Kick chat during a stream and check
      the answers match the panel (deck name and code, rank label, today's record). Type `!deck` twice within 10 s: the
      second one must get no answer (`204`). Type `!dust`: nothing (it is off by default) until you enable it in
      Settings → Chat commands. Change the language in Settings to English and check `!rank` answers in English without
      restarting anything. Anything that differs → paste the Streamer.bot action log and the URL you used.

