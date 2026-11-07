#!/bin/sh
# Sets up the user the release workflow deploys the career service as: deploy-user-setup.sh <public key file> [user@host]
# Run from a computer with full access to the machine. The deploy user can copy the service as arcanum, set the game
# version it checks (arcanum-set-game), take a backup and restart the service; nothing else as root (no unit changes:
# deploy.sh asks for a full-access run when the unit changed).
set -eu
key=$(cat "$1")
host=${2:-ubuntu@arcanum-career.duckdns.org}
here=$(dirname "$0")
scp -q "$here/arcanum-set-game.sh" "$host:/tmp/"
ssh "$host" "set -eu
  id deploy >/dev/null 2>&1 || sudo useradd --system --create-home --shell /bin/sh deploy
  sudo install -d -o deploy -g deploy -m 700 ~deploy/.ssh
  echo 'restrict $key' | sudo tee ~deploy/.ssh/authorized_keys >/dev/null
  sudo chown deploy:deploy ~deploy/.ssh/authorized_keys
  sudo chmod 600 ~deploy/.ssh/authorized_keys
  sudo install -m 755 /tmp/arcanum-set-game.sh /usr/local/sbin/arcanum-set-game
  rm /tmp/arcanum-set-game.sh
  printf '%s\n' \
    'deploy ALL=(arcanum) NOPASSWD: /usr/bin/rsync' \
    'deploy ALL=(root) NOPASSWD: /usr/local/sbin/arcanum-set-game, /usr/bin/systemctl start arcanum-backup.service, /usr/bin/systemctl restart arcanum-career' \
    > /tmp/arcanum-deploy.sudoers
  sudo visudo -cf /tmp/arcanum-deploy.sudoers
  sudo install -m 440 /tmp/arcanum-deploy.sudoers /etc/sudoers.d/arcanum-deploy
  rm /tmp/arcanum-deploy.sudoers
  echo 'Known host line for the workflow:'
  ssh-keyscan -t ed25519 localhost 2>/dev/null | sed 's/^localhost/arcanum-career.duckdns.org/'"
