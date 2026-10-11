#!/bin/sh
# Builds the career service and installs it on its machine: deploy.sh [user@host]
set -eu
host=${1:-ubuntu@arcanum-career.duckdns.org}
here=$(dirname "$0")
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
dotnet publish "$here/.." -c Release -r linux-x64 --self-contained false -o "$out"
rsync -az --delete --rsync-path="sudo -u arcanum rsync" "$out/" "$host:/opt/arcanum-career/"
ssh "$host" 'sudo systemctl restart arcanum-career && sleep 2 && curl -fsS http://127.0.0.1:5080/health && echo'
