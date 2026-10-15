#!/usr/bin/env bash
# Builds the HDT exporter plugin in Release and packs it as a zip ready to drop into HDT's Plugins folder:
#   DustPlanner.HdtExporter-v<version>.zip
#     DustPlanner/DustPlanner.HdtExporter.dll
#     DustPlanner/DustPlanner.HdtExporter.Logic.dll
#     DustPlanner/DustPlanner.Contracts.dll
#     DustPlanner/INSTALL.txt
# Usage: tools/package-exporter.sh [output-dir]   (requires lib/hdt to be populated)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/release}"
PROJECT="$ROOT/src/DustPlanner.HdtExporter/DustPlanner.HdtExporter.csproj"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
if [[ -z "$VERSION" ]]; then VERSION="0.0.0"; fi

dotnet build "$PROJECT" -c Release -nologo -v q
BIN="$(dotnet msbuild "$PROJECT" -getProperty:OutDir -p:Configuration=Release -nologo)"
BIN="${BIN//\\//}"          # MSBuild prints backslashes
BIN="${BIN%/}"
if [[ "$BIN" != /* ]]; then BIN="$ROOT/src/DustPlanner.HdtExporter/$BIN"; fi

mkdir -p "$STAGE/DustPlanner" "$OUT"
for f in DustPlanner.HdtExporter.dll DustPlanner.HdtExporter.Logic.dll DustPlanner.Contracts.dll; do
  cp "$BIN/$f" "$STAGE/DustPlanner/$f"
done
cat > "$STAGE/DustPlanner/INSTALL.txt" <<EOF
DustPlanner Exporter v$VERSION — Hearthstone Deck Tracker plugin
================================================================

1. Close Hearthstone Deck Tracker.
2. Copy this whole "DustPlanner" folder into:
       %AppData%\HearthstoneDeckTracker\Plugins\
   so that you end up with ...\Plugins\DustPlanner\DustPlanner.HdtExporter.dll
3. Start HDT, open Options > Tracker > Plugins and enable "DustPlanner Exporter".
4. A "DustPlanner" entry appears in the plugin menu: export now, export diagnostics, open export folder.

Exports are written to %LocalAppData%\DustPlanner\inbox\<account>\ as collection.json, decks.json, rank.json.
Settings: %LocalAppData%\DustPlanner\exporter-settings.json. Log: %LocalAppData%\DustPlanner\exporter.log.

This plugin is read-only: it never sends input to the game and never reads game memory on its own.
It only uses HDT's public plugin API. See SECURITY.md in the repository.
Not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard Entertainment.
EOF

ZIP="$OUT/DustPlanner.HdtExporter-v$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -q -r "$ZIP" DustPlanner)
sha256sum "$ZIP" > "$ZIP.sha256"
echo "Packaged $ZIP"
