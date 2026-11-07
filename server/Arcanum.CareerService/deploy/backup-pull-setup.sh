#!/bin/sh
# Sets up the backup machine (always on, Linux with systemd): run there as root, from this folder. Makes the user
# arcanum-backup and its pull key, installs backup-pull.sh with a daily timer (04:30 UTC) and prints the public key to
# hand to backup-setup.sh on the service's machine. The copies are kept in /var/lib/arcanum-backup/arcanum-career-backups.
set -eu
here=$(dirname "$0")
home=/var/lib/arcanum-backup
id arcanum-backup >/dev/null 2>&1 || useradd --system --create-home --home-dir "$home" --shell /usr/sbin/nologin arcanum-backup
install -d -o arcanum-backup -g arcanum-backup -m 700 "$home/.ssh"
[ -f "$home/.ssh/arcanum-backup" ] || runuser -u arcanum-backup -- ssh-keygen -q -t ed25519 -N '' -C arcanum-backup-pull -f "$home/.ssh/arcanum-backup"
install -m 755 "$here/backup-pull.sh" /usr/local/bin/arcanum-backup-pull
cat > /etc/systemd/system/arcanum-backup-pull.service <<EOF
[Unit]
Description=Fetch the Arcanum career database backups
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=arcanum-backup
Environment=HOME=$home
ExecStart=/usr/local/bin/arcanum-backup-pull
EOF
cat > /etc/systemd/system/arcanum-backup-pull.timer <<EOF
[Unit]
Description=Daily fetch of the Arcanum career database backups

[Timer]
OnCalendar=*-*-* 04:30:00 UTC
Persistent=true

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now arcanum-backup-pull.timer
echo "Public key for backup-setup.sh:"
cat "$home/.ssh/arcanum-backup.pub"
