#!/usr/bin/env bash
# Builds the DustPlanner HDT plugin in Release, publishes the DustPlanner service next to it (self-contained
# win-x64, no runtime to install) and packs both as one folder ready to drop into HDT's Plugins folder:
#   DustPlanner-v<version>.zip
#     DustPlanner/DustPlanner.HdtExporter.dll
#     DustPlanner/DustPlanner.HdtExporter.Logic.dll
#     DustPlanner/DustPlanner.Contracts.dll
#     DustPlanner/service/dustplanner-host.exe     (panel, JSON API, OBS overlay, automatic sync)
#     DustPlanner/INSTALL.txt
# Usage: tools/package-exporter.sh [output-dir] [--no-service]   (requires lib/hdt to be populated)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/release"
WITH_SERVICE=1
for arg in "$@"; do
  case "$arg" in
    --no-service) WITH_SERVICE=0 ;;
    *) OUT="$arg" ;;
  esac
done
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"      # absolute: the zip step runs from a temp dir
PROJECT="$ROOT/src/DustPlanner.HdtExporter/DustPlanner.HdtExporter.csproj"
HOST_PROJECT="$ROOT/src/DustPlanner.Host/DustPlanner.Host.csproj"
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

if [[ "$WITH_SERVICE" == 1 ]]; then
  dotnet publish "$HOST_PROJECT" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -p:GenerateDocumentationFile=false -o "$STAGE/service-publish" -nologo -v q
  mkdir -p "$STAGE/DustPlanner/service"
  cp "$STAGE/service-publish/dustplanner-host.exe" "$STAGE/DustPlanner/service/dustplanner-host.exe"
fi

cat > "$STAGE/DustPlanner/INSTALL.txt" <<EOT
DustPlanner v$VERSION — Hearthstone Deck Tracker plugin
======================================================

1. Close Hearthstone Deck Tracker.
2. Copy this whole "DustPlanner" folder into:
       %AppData%\HearthstoneDeckTracker\Plugins\
   so that you end up with ...\Plugins\DustPlanner\DustPlanner.HdtExporter.dll
3. Start HDT, open Options > Tracker > Plugins and enable "DustPlanner".
4. The plugin exports your collection whenever you leave the game's Collection screen and starts the
   DustPlanner service (service\dustplanner-host.exe, stopped together with HDT). The "DustPlanner" entry
   in the plugin menu opens the panel — what you are missing for a deck, what to disenchant, history,
   games — or asks about the deck selected in HDT.

Data: %LocalAppData%\DustPlanner\ (inbox\<account>\ exports, dustplanner.db, prefs.json, meta.txt).
Settings: %LocalAppData%\DustPlanner\exporter-settings.json. Log: %LocalAppData%\DustPlanner\exporter.log.

This plugin is read-only: it never sends input to the game and never reads game memory on its own.
It only uses HDT's public plugin API. See SECURITY.md in the repository.
Not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard Entertainment.
EOT

ZIP="$OUT/DustPlanner-v$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -q -r "$ZIP" DustPlanner)
sha256sum "$ZIP" > "$ZIP.sha256"
echo "Packaged $ZIP"
