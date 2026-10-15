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
