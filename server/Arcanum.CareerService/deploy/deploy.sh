#!/bin/sh
# Builds the career service and installs it on its machine: deploy.sh [user@host]
set -eu
host=${1:-ubuntu@arcanum-career.duckdns.org}
here=$(dirname "$0")
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
dotnet publish "$here/.." -c Release -r linux-x64 --self-contained false -o "$out"
rsync -az --delete --rsync-path="sudo -u arcanum rsync" "$out/" "$host:/opt/arcanum-career/"
# The career rules of this build are the game version's: careers are checked only for that version.
game=$(sed -n 's/^config\/version="\(.*\)"/\1/p' "$here/../../../project.godot")
scp -q "$here/arcanum-career.service" "$host:/tmp/arcanum-career.service"
ssh "$host" "echo Career__Game=$game | sudo tee /etc/arcanum-career/game.env >/dev/null && sudo install -m 644 /tmp/arcanum-career.service /etc/systemd/system/arcanum-career.service && sudo systemctl daemon-reload"
ssh "$host" 'sudo systemctl restart arcanum-career && sleep 3 && curl -fsS http://127.0.0.1:5080/health && echo'
