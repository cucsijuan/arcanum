#!/bin/sh
# Fetches the career database's backups off the service's machine and keeps them (run daily on the backup machine by
# arcanum-backup-pull.timer): backup-pull.sh [destination, default ~/arcanum-career-backups] [host]
# Kept: every copy of the last 7 days, the newest of each week for 5 weeks, the newest of each month for a year.
set -eu
dest=${1:-$HOME/arcanum-career-backups}
host=${2:-arcanum-career.duckdns.org}
mkdir -p "$dest"
rsync -a --include='career-*.db.gz' --exclude='*' \
  -e "ssh -i $HOME/.ssh/arcanum-backup -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new" "arcanum-backup@$host:" "$dest/"
for f in "$dest"/career-*.db.gz; do
  [ -e "$f" ] || continue
  gzip -t "$f" || { echo "damaged copy: $f" >&2; exit 1; }
done
today=$(date -u +%s)
seen=""
# Newest first: the first copy of a week or a month is the one kept for it.
for f in $(ls -1 "$dest"/career-*.db.gz 2>/dev/null | sort -r); do
  day=$(basename "$f" | cut -c8-17)
  age=$(( (today - $(date -u -d "$day" +%s)) / 86400 ))
  week=w$(date -u -d "$day" +%G-%V)
  month=m$(date -u -d "$day" +%Y-%m)
  keep=no
  [ "$age" -le 7 ] && keep=yes
  case " $seen " in *" $week "*) ;; *) [ "$age" -le 35 ] && keep=yes; seen="$seen $week" ;; esac
  case " $seen " in *" $month "*) ;; *) [ "$age" -le 365 ] && keep=yes; seen="$seen $month" ;; esac
  [ "$keep" = yes ] || rm -f "$f"
done
newest=$(ls -1 "$dest"/career-*.db.gz 2>/dev/null | sort | tail -1)
[ -n "$newest" ] || { echo "no backups in $dest" >&2; exit 1; }
# A machine that stopped backing up shows here: the newest copy must be from the last two days.
age=$(( (today - $(date -u -d "$(basename "$newest" | cut -c8-17)" +%s)) / 86400 ))
[ "$age" -le 1 ] || { echo "newest backup is $age days old: $newest" >&2; exit 1; }
echo "newest backup: $newest"
