# DustPlanner

A read-only dust planner for Hearthstone: tell it which deck you want to build, and it tells you which
cards you are missing, how much Arcane Dust you need, and which cards in your collection you could
disenchant to get there with the least regret.

> **Status: work in progress.** Stage 0 of 8 — the HDT exporter plugin and the shared contract exist; the
> planner itself is not implemented yet. See [`docs/PROGRESO.md`](docs/PROGRESO.md).

## What it does — and what it never does

- Reads your collection, decks and rank **through Hearthstone Deck Tracker's public plugin API** and writes them to JSON.
- Computes missing cards and an optimal disenchant plan that respects your decks, the current meta and your preferences.
- **Never** clicks, types, injects, or reads the game's memory on its own. You disenchant by hand, in the game.
  The full policy is in [`SECURITY.md`](SECURITY.md).

## Repository layout

| Path | Target | Role |
|---|---|---|
| `src/DustPlanner.Contracts` | netstandard2.0 | JSON contract shared by the exporter and the core |
| `src/DustPlanner.HdtExporter.Logic` | netstandard2.0 | Exporter logic without HDT dependencies (testable) |
| `src/DustPlanner.HdtExporter` | net472 | Hearthstone Deck Tracker plugin (exporter) |
| `src/DustPlanner.Core` | net10.0 | Card data, deckstrings, collection, planner, meta *(stage 1+)* |
| `src/DustPlanner.Store` | net10.0 | SQLite persistence *(stage 5)* |
| `src/DustPlanner.Cli` | net10.0 | Command-line interface *(stage 2+)* |
| `src/DustPlanner.LogTracker` | net10.0 | `Power.log` game tracker *(stage 6)* |
| `src/DustPlanner.Mcp` | net10.0 | Read-only MCP server *(stage 7)* |
| `src/DustPlanner.Host` | net10.0 | Local HTTP host + OBS overlay *(stage 8)* |
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

## Installing the exporter plugin

See [`docs/VERIFICACION.md`](docs/VERIFICACION.md) §A for the step-by-step until a release zip is published.

## License and disclaimer

MIT — see [`LICENSE`](LICENSE).

Not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard
Entertainment, Inc. Card data is fetched at runtime from [HearthstoneJSON](https://hearthstonejson.com/)
and is © Blizzard Entertainment; none of it is redistributed in this repository.
