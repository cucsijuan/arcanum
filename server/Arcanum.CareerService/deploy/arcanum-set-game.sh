#!/bin/sh
# Writes the game version the career service checks (/etc/arcanum-career/game.env), run by deploy.sh with sudo:
# installed as /usr/local/sbin/arcanum-set-game, the one root command the deploy user may run besides the restarts.
# When the version changes, the one before it and the time of the change are kept: careers played with the previous
# version before then are taken as it computed them where today's rules differ (see CareerChecker).
set -eu
game=${1:?game version}
echo "$game" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || { echo "not a version: $game" >&2; exit 1; }
env=/etc/arcanum-career/game.env
old=$(sed -n 's/^Career__Game=//p' "$env" 2>/dev/null || true)
[ "$old" = "$game" ] && exit 0
printf 'Career__Game=%s\nCareer__PreviousGame=%s\nCareer__Since=%s\n' "$game" "$old" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$env.new"
chmod 644 "$env.new"
mv "$env.new" "$env"
echo "career service checks game $game (previous $old)"
