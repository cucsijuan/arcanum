#!/bin/sh
# Publishes a content module's season (career/season.json) after checking it against the module and against the
# published one (days already played can't change): publish-season.sh <module dir> [user@host]
set -eu
module=$1
host=${2:-ubuntu@arcanum-career.duckdns.org}
here=$(dirname "$0")
id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "$module/manifest.json")
file="$module/career/season.json"
published=$(mktemp)
trap 'rm -f "$published"' EXIT
curl -fsS "https://arcanum-career.duckdns.org/career/season/$id.json" -o "$published" || : > "$published"
dotnet run --project "$here/../../../tools/SeasonCheck" -- "$module" "$file" "$published"
scp -q "$file" "$host:/tmp/season.json"
ssh "$host" "sudo install -D -m 644 /tmp/season.json /srv/arcanum-career/career/season/$id.json && rm /tmp/season.json"
echo "published $id's season"
