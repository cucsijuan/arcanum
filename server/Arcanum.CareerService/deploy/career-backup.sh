#!/bin/sh
# Copies the career database while the service runs (installed as /usr/local/bin/career-backup by backup-setup.sh, run
# by arcanum-backup.service): a checked, compressed copy in /var/lib/arcanum-career/backups, the last 3 days kept there.
# cucsiserver pulls them off the machine (backup-pull.sh).
set -eu
umask 027
state=/var/lib/arcanum-career
dir=$state/backups
name=career-$(date -u +%Y-%m-%d-%H%M).db
tmp=$dir/.$name
trap 'rm -f "$tmp" "$tmp.gz"' EXIT
sqlite3 "$state/career.db" ".backup '$tmp'"
check=$(sqlite3 "$tmp" "PRAGMA integrity_check;")
[ "$check" = ok ] || { echo "backup failed the integrity check: $check" >&2; exit 1; }
gzip -9 "$tmp"
mv "$tmp.gz" "$dir/$name.gz"
find "$dir" -name 'career-*.db.gz' -mtime +2 -delete
echo "backed up $name.gz"
