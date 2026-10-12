#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Starts the season editor (tools/SeasonEditor) and opens it in the browser; Ctrl+C stops it.
#
# usage: tools/season-editor.sh [module dir] [user@host]
#   module dir: the content module checkout (default: ../arcanum-classic next to this repository)
#   user@host:  the career service's server (default: the editor's own)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
module="${1:-$root/../arcanum-classic}"
[[ -f "$module/manifest.json" ]] || { echo "error: no content module at $module (pass its folder)" >&2; exit 1; }
url="http://127.0.0.1:5099"

if curl -s -o /dev/null "$url"; then
    echo "The season editor is already running: $url"
    xdg-open "$url" >/dev/null 2>&1 || true
    exit 0
fi

dotnet run --project "$root/tools/SeasonEditor" -- "$(cd "$module" && pwd)" ${2:+"$2"} &
editor=$!
trap 'kill $editor 2>/dev/null; wait $editor 2>/dev/null' EXIT INT TERM

echo "Starting the season editor…"
until curl -s -o /dev/null "$url"; do
    kill -0 $editor 2>/dev/null || { echo "error: the season editor stopped (see above)" >&2; exit 1; }
    sleep 0.5
done
echo "Season editor: $url (Ctrl+C to stop)"
xdg-open "$url" >/dev/null 2>&1 || echo "Open $url in your browser."
wait $editor
