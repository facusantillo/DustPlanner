# Installing DustPlanner (HDT plugin)

DustPlanner ships as one plugin for [Hearthstone Deck Tracker](https://hsreplay.net/downloads/). It exports your
collection, decks and ranked standing to JSON, starts the DustPlanner service (the panel, the JSON API, the OBS overlay
and the automatic sync — `service\dustplanner-host.exe`, self-contained, nothing else to install) and opens the panel
from HDT's plugin menu. It is read-only: it never clicks, types or reads game memory on its own — it only consumes what
HDT already holds (see [`SECURITY.md`](../SECURITY.md)).

## From a release zip

1. Download `DustPlanner.zip` from [dustplanner.app](https://dustplanner.app/) (it is the latest release's
   `DustPlanner-v<version>.zip` under a fixed name) and, optionally, check its `.sha256` on the releases page.
2. Close HDT.
3. Extract the zip. Copy the `DustPlanner` folder into `%AppData%\HearthstoneDeckTracker\Plugins\` so that
   `...\Plugins\DustPlanner\DustPlanner.HdtExporter.dll` exists (the folder also holds
   `DustPlanner.HdtExporter.Logic.dll`, `DustPlanner.Contracts.dll`, `service\dustplanner-host.exe` and `INSTALL.txt`).
4. Start HDT → **Options → Tracker → Plugins** → enable **DustPlanner**.

## From source (against the HDT version you run)

```powershell
tools\fetch-hdt-refs.ps1 -FromInstall      # copies HDT's assemblies from %LocalAppData%\HearthstoneDeckTracker\app-<version>
tools\package-exporter.ps1 -Install        # builds the plugin, publishes the service, zips, and copies into HDT's Plugins folder (HDT must be closed)
tools\package-exporter.ps1 -Install -NoService   # plugin only (the service is started from your own build, see below)
```

## Using it

- **Plugins → DustPlanner → Open DustPlanner** (or the plugin's button in the Plugins list) opens the panel in your
  browser: Home, Deck, History, Games, Settings. **What am I missing for the selected deck?** opens the Deck page for
  the deck selected in HDT's deck list.
- The plugin exports when you enter or leave the game's Collection screen; the service imports every new export on its
  own (the Home page shows *Last sync*), so a saved plan turns *partial* / *confirmed* without any action.
- The service runs on `http://127.0.0.1:8765` (loopback only; the next free port if 8765 is taken — the plugin reads
  the real address from `%LocalAppData%\DustPlanner\service.json`). It is started when HDT loads the plugin and ends
  with HDT. To run the service yourself instead (a build from source, say), set `"start_service": false` in
  `exporter-settings.json` and start `dotnet run --project src\DustPlanner.Host -c Release -- --open`; `"service_path"`
  points the plugin at another executable.

## What happens next

- With Hearthstone running, open **My Collection** once and go back to the menu. HDT re-reads the collection from the
  game only when you **enter or leave the Collection screen** (and after pack opening); the plugin exports on that
  event. So after disenchanting or crafting, **go back to the main menu** — nothing is exported while you stay in the
  Collection. Visiting **Play** once makes the in-game decks available.
- *Export now* (plugin menu) writes what HDT currently holds, bypassing the change detection; it does not make HDT
  re-read the game. Use it when `dustplanner sync` still reports the previous snapshot after you left the Collection.
- A changed collection is always written; the `min_interval_seconds` rate limit only applies to decks and rank.
- Files land in `%LocalAppData%\DustPlanner\inbox\<hi>-<lo>\`:
  `collection.json`, `decks.json`, `rank.json`, `active-deck.json` (the deck selected in HDT; no history, `deck: null`
  when none is selected or it is an Arena deck), plus `history\` with timestamped copies (30 kept per kind).
- The plugin menu (**DustPlanner**) offers *Export now*, *Export diagnostics* (writes `diagnostics.json`, used to confirm
  what HDT's premium slots 2/3 mean — see [`VERIFICACION.md`](VERIFICACION.md) §A3) and *Open export folder*.
- Settings: `%LocalAppData%\DustPlanner\exporter-settings.json`
  (`output_directory`, `history_keep`, `min_interval_seconds`, `export_on_mode_change`, `include_hdt_decks`,
  `start_service`, `service_port`, `service_path`).
- Logs: `%LocalAppData%\DustPlanner\exporter.log` (the plugin; the first line after start says which HDT events were
  hooked) and `%LocalAppData%\DustPlanner\service.log` (the service it starts: panel, sync, Kick connection; 2 MB,
  one previous copy kept as `service.log.1`).

## Compatibility

The plugin is compiled against HDT 1.55.6 (latest release on GitHub) and every API it uses was verified against the
1.59.2 source. If a future HDT build removes or changes one of those APIs, the plugin still loads: the affected feature
is disabled, the status line in the menu says so, and `meta.capabilities` in each export records which features were
available. Run `dotnet run --project tools/HdtApiCheck -- "<HDT app folder>"` to check a specific HDT build.

## Uninstall

Close HDT and delete `%AppData%\HearthstoneDeckTracker\Plugins\DustPlanner`. Exports under `%LocalAppData%\DustPlanner`
can be deleted independently.
