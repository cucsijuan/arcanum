#!/bin/sh
# Puts a content module version on the career service, which checks careers with it:
#   publish-content.sh <module dir> [user@host] [card data file]
# The module goes as it is in the folder (its manifest's version names it); the card data is the game's compact import of
# that version (by default the one this computer's game made: user://card_data/<id>/cards.v14-<version>.jsonl.gz).
set -eu
module=${1:?module dir}
host=${2:-ubuntu@arcanum-career.duckdns.org}
id=$(sed -n 's/.*"id": *"\([^"]*\)".*/\1/p' "$module/manifest.json" | head -1)
version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$module/manifest.json" | head -1)
cards=${3:-$HOME/.local/share/godot/app_userdata/Arcanum/card_data/$id/cards.v14-$version.jsonl.gz}
[ -f "$cards" ] || { echo "No card data at $cards: run the game once with this module version, or pass the file." >&2; exit 1; }
dir=/var/lib/arcanum-career/content/$id/$version
ssh "$host" "sudo -u arcanum mkdir -p $dir/module"
rsync -az --delete --exclude .git --rsync-path="sudo -u arcanum rsync" "$module/" "$host:$dir/module/"
rsync -az --rsync-path="sudo -u arcanum rsync" "$cards" "$host:$dir/cards.jsonl.gz"
echo "Published $id $version to $host:$dir"
