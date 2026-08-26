#!/usr/bin/env bash
# Build HuntTally on macOS.
#
# The Dalamud SDK needs to reference Dalamud.dll. On Windows it finds it
# automatically; everywhere else you point it there with DALAMUD_HOME.

set -euo pipefail

if [[ -z "${DALAMUD_HOME:-}" ]]; then
  echo "DALAMUD_HOME not set, searching for a Dalamud install..."

  # Common locations: XIV on Mac keeps its install under Application Support;
  # XIVLauncher.Core uses ~/.xlcore.
  CANDIDATES=$(find \
    "$HOME/Library/Application Support/XIV on Mac" \
    "$HOME/.xlcore" \
    -type f -name "Dalamud.dll" -path "*dev*" 2>/dev/null || true)

  if [[ -z "$CANDIDATES" ]]; then
    echo "Could not find Dalamud.dll." >&2
    echo "Launch the game once with Dalamud enabled so it downloads, then set" >&2
    echo "DALAMUD_HOME manually to the directory containing Dalamud.dll." >&2
    exit 1
  fi

  export DALAMUD_HOME="$(dirname "$(echo "$CANDIDATES" | head -n1)")"
  echo "Found: $DALAMUD_HOME"
fi

dotnet build -c Release

echo
echo "Built to: $(pwd)/bin/Release/HuntTally/"
