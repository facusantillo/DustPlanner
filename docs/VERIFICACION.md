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

- [ ] A1.1 HDT starts with the plugin enabled and shows the menu.
- [ ] A1.2 `%LocalAppData%\DustPlanner\exporter.log` exists and its first line reads
      `Loaded v0.1.0.0 (HDT <your version>, HearthMirror <version>). collection event: on, mode event: on`.
      If any says `OFF`, paste the log line into an issue — it means the HDT version you run changed an API.

### A2. First export

1. Start Hearthstone, log in, open **My Collection** once, then go back to the main menu.
2. HDT refreshes its collection when you leave the collection screen; the plugin exports on that event.
   If nothing appears within a minute, use **DustPlanner → Export … now**.

- [ ] A2.1 `%LocalAppData%\DustPlanner\inbox\<hi>-<lo>\collection.json` exists.
- [ ] A2.2 `meta.account.battle_tag` and `meta.account.region` are yours.
- [ ] A2.3 `dust` matches the dust shown in the game's crafting screen.
- [ ] A2.4 Pick three cards you own (one regular ×2, one golden, one legendary ×1) and check their
      `dbfId → [n0, n1, …]` rows: index 0 = regular copies, index 1 = golden copies.
- [ ] A2.5 `decks.json` exists after visiting the **Play** screen once, and lists your in-game decks
      (`origin: "ingame"`) and HDT's saved decks (`origin: "hdt"`).
- [ ] A2.6 `rank.json` exists and `formats.standard.star_level` / `league_id` match your ladder standing.
- [ ] A2.7 `history\` contains timestamped copies and never grows past 30 files per kind.

### A3. Diagnostics — the two open data questions (Q2, Q4)

Run **DustPlanner → Export diagnostics** with the game open and the collection loaded. It writes
`inbox\<hi>-<lo>\diagnostics.json`. Send me that file (it contains card names and counts, nothing personal
beyond your BattleTag — remove `meta.account` if you prefer).

- [ ] A3.1 **Premium slots 2 and 3 (Q4).** `premium_types` lists how many cards have `premium_type` 2 and 3.
      For each card in `samples` with a non-zero value at index 2 or 3 of `raw_counts`, look it up in the game
      and note whether that copy is **Signature** or **Diamond**. Expected outcome: one mapping such as
      `2 = signature, 3 = diamond`. Until this is recorded in `data/premium-slots.json` (stage 1), copies in
      slots 2/3 have *unknown* dust value and are excluded from every recommendation.
- [ ] A3.2 **Core set (Q2).** `core_set_present` tells whether CORE-set cards appear in the export at all,
      and `core_set_sample_names` lists a few. Either outcome is supported; this only decides whether the
      "missing cards" report can distinguish *Core card you have not unlocked* from *Core card not exported*.
- [ ] A3.3 If `notes` contains *"No card has copies in premium slots 2 or 3"*, you own no signature/diamond
      cards and A3.1 cannot be answered from your collection. The conservative exclusion then costs nothing.

### A4. Unopened packs (correction 1)

No HDT / HearthMirror API exposes the number of unopened packs (verified on 1.55.6 binaries and 1.59.2
source: only `GetPackCards`, the pack being opened, and `GetLastOpenedBoosterId` exist). The export always
writes `unopened_packs: null` and every plan prints the "open your packs first" warning without a count.

- [ ] A4.1 Confirm you are fine with the warning having no count, or tell me if you know of a source.

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

- [ ] B1. Run the check against your own HDT and attach the report:
  ```powershell
  dotnet run --project tools\HdtApiCheck -- "$env:LOCALAPPDATA\HearthstoneDeckTracker\app-<version>" --report hdt-api-report.md
  ```
  Expected last line: **Result: all required APIs present.**

---

## C. Pending for later stages (filled in as stages land)

- [ ] C1. Catalog download on your machine: `dotnet run --project src/DustPlanner.Cli -- catalog update` must print
      `Catalog build <number> (enUS): <N> collectible cards`. Then `dustplanner collection show` and
      `dustplanner need <deck code>`; compare the missing list and dust with the in-game crafting screen for one deck.
      Paste back any line starting with `Sets not known to data/non-craftable.json` — those set names need a decision.
- [ ] C2. Signature / diamond dust values (craft and disenchant) — unknown; excluded until confirmed (stage 1/4).
- [ ] C3. Golden-card refund amount for nerfed cards (assumed = golden craft cost) — **to verify** (stage 4).
- [ ] C4. 20 real games recorded from `Power.log` compared with HDT's history (stage 6).
- [ ] C5. OBS Browser Source renders the overlay with a transparent background at 1080p (stage 8).
