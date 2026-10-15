# Installing the DustPlanner exporter (HDT plugin)

The exporter is a plugin for [Hearthstone Deck Tracker](https://hsreplay.net/downloads/). It writes your collection,
decks and ranked standing to JSON files that the DustPlanner CLI reads. It is read-only: it never clicks, types or reads
game memory on its own — it only consumes what HDT already holds (see [`SECURITY.md`](../SECURITY.md)).

## From a release zip

1. Download `DustPlanner.HdtExporter-v<version>.zip` from the GitHub Releases page and, optionally, check its
   `.sha256`.
2. Close HDT.
3. Extract the zip. Copy the `DustPlanner` folder into `%AppData%\HearthstoneDeckTracker\Plugins\` so that
   `...\Plugins\DustPlanner\DustPlanner.HdtExporter.dll` exists (the folder also holds
   `DustPlanner.HdtExporter.Logic.dll`, `DustPlanner.Contracts.dll` and `INSTALL.txt`).
4. Start HDT → **Options → Tracker → Plugins** → enable **DustPlanner Exporter**.

## From source (against the HDT version you run)

```powershell
tools\fetch-hdt-refs.ps1 -FromInstall      # copies HDT's assemblies from %LocalAppData%\HearthstoneDeckTracker\app-<version>
tools\package-exporter.ps1 -Install        # builds, zips, and copies the plugin into HDT's Plugins folder (HDT must be closed)
```

## What happens next

- With Hearthstone running, open **My Collection** once and go back to the menu. HDT re-reads the collection from the
  game only when you **enter or leave the Collection screen** (and after pack opening); the plugin exports on that
  event. So after disenchanting or crafting, **go back to the main menu** — nothing is exported while you stay in the
  Collection. Visiting **Play** once makes the in-game decks available.
- *Export now* (plugin menu) writes what HDT currently holds, bypassing the change detection; it does not make HDT
  re-read the game. Use it when `dustplanner sync` still reports the previous snapshot after you left the Collection.
- A changed collection is always written; the `min_interval_seconds` rate limit only applies to decks and rank.
- Files land in `%LocalAppData%\DustPlanner\inbox\<hi>-<lo>\`:
  `collection.json`, `decks.json`, `rank.json`, plus `history\` with timestamped copies (30 kept per kind).
- The plugin menu (**DustPlanner**) offers *Export now*, *Export diagnostics* (writes `diagnostics.json`, used to confirm
  what HDT's premium slots 2/3 mean — see [`VERIFICACION.md`](VERIFICACION.md) §A3) and *Open export folder*.
- Settings: `%LocalAppData%\DustPlanner\exporter-settings.json`
  (`output_directory`, `history_keep`, `min_interval_seconds`, `export_on_mode_change`, `include_hdt_decks`).
- Log: `%LocalAppData%\DustPlanner\exporter.log`. The first line after start says which HDT events were hooked.

## Compatibility

The plugin is compiled against HDT 1.55.6 (latest release on GitHub) and every API it uses was verified against the
1.59.2 source. If a future HDT build removes or changes one of those APIs, the plugin still loads: the affected feature
is disabled, the status line in the menu says so, and `meta.capabilities` in each export records which features were
available. Run `dotnet run --project tools/HdtApiCheck -- "<HDT app folder>"` to check a specific HDT build.

## Uninstall

Close HDT and delete `%AppData%\HearthstoneDeckTracker\Plugins\DustPlanner`. Exports under `%LocalAppData%\DustPlanner`
can be deleted independently.
