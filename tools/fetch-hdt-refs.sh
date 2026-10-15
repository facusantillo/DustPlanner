#!/usr/bin/env bash
# Downloads the HDT release used as compile-time reference for the exporter and copies the four
# assemblies the plugin compiles against into lib/hdt/. Nothing from this folder is ever committed.
#
# Usage: tools/fetch-hdt-refs.sh [version]      (default: see HDT_REF_VERSION below)
set -euo pipefail

HDT_REF_VERSION="${1:-1.55.6}"
# SHA-256 of Hearthstone.Deck.Tracker-v1.55.6.zip as downloaded on 2026-10-15.
declare -A KNOWN_SHA256=(
  ["1.55.6"]="3ebf12fa3d5e842f1fa8ce433758640e3ce2903a400dac5fb096822149d2bb60"
)

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/lib/hdt"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://github.com/HearthSim/Hearthstone-Deck-Tracker/releases/download/v${HDT_REF_VERSION}/Hearthstone.Deck.Tracker-v${HDT_REF_VERSION}.zip"
echo "Downloading $URL"
curl -fsSL -o "$TMP/hdt.zip" "$URL"

if [[ -n "${KNOWN_SHA256[$HDT_REF_VERSION]:-}" ]]; then
  echo "${KNOWN_SHA256[$HDT_REF_VERSION]}  $TMP/hdt.zip" | sha256sum -c -
else
  echo "warning: no known checksum for $HDT_REF_VERSION; skipping verification" >&2
fi

unzip -q "$TMP/hdt.zip" -d "$TMP/x"
SRC="$TMP/x/Hearthstone Deck Tracker"
mkdir -p "$DEST"
for f in "Hearthstone Deck Tracker.exe" "HearthMirror.dll" "HearthDb.dll" "Newtonsoft.Json.dll"; do
  cp "$SRC/$f" "$DEST/$f"
done
echo "$HDT_REF_VERSION" > "$DEST/VERSION"
echo "HDT $HDT_REF_VERSION references copied to $DEST"
