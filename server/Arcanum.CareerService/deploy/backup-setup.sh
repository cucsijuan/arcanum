#!/bin/sh
# Sets up the career database's daily backups on the service's machine: backup-setup.sh <puller's public key file> [user@host]
# The machine keeps the last 3 days; the puller (backup-pull.sh) logs in as arcanum-backup, which can only read the
# backups folder (rrsync -ro), and keeps the copies off the machine.
set -eu
key=$(cat "$1")
host=${2:-ubuntu@arcanum-career.duckdns.org}
here=$(dirname "$0")
scp -q "$here/career-backup.sh" "$here/arcanum-backup.service" "$here/arcanum-backup.timer" "$host:/tmp/"
ssh "$host" "set -eu
  sudo apt-get install -y sqlite3 >/dev/null
  id arcanum-backup >/dev/null 2>&1 || sudo useradd --system --create-home --shell /bin/sh arcanum-backup
  sudo install -d -o arcanum -g arcanum-backup -m 2750 /var/lib/arcanum-career/backups
  sudo install -d -o arcanum-backup -g arcanum-backup -m 700 ~arcanum-backup/.ssh
  echo 'restrict,command=\"/usr/bin/rrsync -ro /var/lib/arcanum-career/backups/\" $key' | sudo tee ~arcanum-backup/.ssh/authorized_keys >/dev/null
  sudo chown arcanum-backup:arcanum-backup ~arcanum-backup/.ssh/authorized_keys
  sudo chmod 600 ~arcanum-backup/.ssh/authorized_keys
  sudo install -m 755 /tmp/career-backup.sh /usr/local/bin/career-backup
  sudo install -m 644 /tmp/arcanum-backup.service /tmp/arcanum-backup.timer /etc/systemd/system/
  rm /tmp/career-backup.sh /tmp/arcanum-backup.service /tmp/arcanum-backup.timer
  sudo systemctl daemon-reload
  sudo systemctl enable --now arcanum-backup.timer
  sudo systemctl start arcanum-backup.service
  sudo ls -l /var/lib/arcanum-career/backups"
