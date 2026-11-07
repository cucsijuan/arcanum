#!/bin/sh
# Builds the career service and installs it on its machine: deploy.sh [user@host]
# Runs as a full-access user (ubuntu, by hand) or as the deploy user (the release workflow; deploy-user-setup.sh), who
# can't change the systemd unit: when the unit changed, deploy.sh stops and asks for a full-access run.
set -eu
host=${1:-ubuntu@arcanum-career.duckdns.org}
here=$(dirname "$0")
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
dotnet publish "$here/.." -c Release -r linux-x64 --self-contained false -o "$out"
if ! ssh "$host" 'cat /etc/systemd/system/arcanum-career.service' | cmp -s - "$here/arcanum-career.service"; then
  case "$host" in
    deploy@*) echo "The service's systemd unit changed: run deploy.sh once as a full-access user." >&2; exit 1 ;;
  esac
  scp -q "$here/arcanum-career.service" "$host:/tmp/arcanum-career.service"
  ssh "$host" 'sudo install -m 644 /tmp/arcanum-career.service /etc/systemd/system/arcanum-career.service && rm /tmp/arcanum-career.service && sudo systemctl daemon-reload'
fi
# A copy of the careers before the new build touches them (backup-setup.sh installs it; skipped where it isn't set up).
ssh "$host" 'if systemctl cat arcanum-backup.service >/dev/null 2>&1; then sudo systemctl start arcanum-backup.service; fi'
rsync -az --delete --rsync-path="sudo -u arcanum rsync" "$out/" "$host:/opt/arcanum-career/"
# The career rules of this build are the game version's: careers are checked only for that version.
game=$(sed -n 's/^config\/version="\(.*\)"/\1/p' "$here/../../../project.godot")
ssh "$host" "sudo /usr/local/sbin/arcanum-set-game $game"
ssh "$host" 'sudo systemctl restart arcanum-career && sleep 3 && curl -fsS http://127.0.0.1:5080/health && echo'
