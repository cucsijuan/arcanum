#!/usr/bin/env bash
# Copies the interface art (a private pack, not part of this repository) into ui-art/.
# Usage: tools/ui-art.sh   (set ARCANUM_UI_ART to the folder holding the art; default ../arcanum-ui-art)
# Without the art the game still runs, drawing plain panels and buttons.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
src="${ARCANUM_UI_ART:-$root/../arcanum-ui-art}"
if [ ! -d "$src" ]; then
  echo "UI art not found at $src (set ARCANUM_UI_ART)" >&2
  exit 1
fi
rm -rf "$root/ui-art"
mkdir -p "$root/ui-art"
for dir in frames buttons dividers scroll general; do
  [ -d "$src/$dir" ] && cp -r "$src/$dir" "$root/ui-art/$dir"
done
echo "UI art copied to $root/ui-art"
