# lib/hdt — HDT compile-time references (not committed)

The exporter plugin compiles against four assemblies shipped with Hearthstone Deck Tracker:

- `Hearthstone Deck Tracker.exe` (installed 1.59.x builds name it `HearthstoneDeckTracker.exe`; the scripts copy it under the release-zip name, which is what the csproj references)
- `HearthMirror.dll`
- `HearthDb.dll`
- `Newtonsoft.Json.dll`

They are **never** committed and never copied into the plugin output (HDT provides them at runtime).
Put them here with one of:

```powershell
# Windows, against the HDT version you actually run (recommended)
tools\fetch-hdt-refs.ps1 -FromInstall

# Any OS, against the latest release published on GitHub (used by CI)
tools/fetch-hdt-refs.sh          # or: tools\fetch-hdt-refs.ps1
```

To check that a given HDT build still exposes every API the exporter uses:

```powershell
dotnet run --project tools/HdtApiCheck -- "$env:LOCALAPPDATA\HearthstoneDeckTracker\app-1.59.2" --report hdt-api-report.md
```
