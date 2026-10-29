#!/bin/sh
# First-time setup of the career service's machine (Ubuntu 24.04): run there with sudo, next to the Caddyfile and the unit.
set -eu
apt-get update
apt-get install -y aspnetcore-runtime-8.0 caddy
id arcanum >/dev/null 2>&1 || useradd --system --home /var/lib/arcanum-career --shell /usr/sbin/nologin arcanum
install -d -o arcanum -g arcanum /opt/arcanum-career
install -d -m 700 /etc/arcanum-career
[ -f /etc/arcanum-career/secrets.env ] || install -m 600 /dev/null /etc/arcanum-career/secrets.env
# The key the service seals checked careers with: made once, never leaves the machine (its public key goes in the game).
if [ ! -f /etc/arcanum-career/seal-key.pem ]; then
  openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out /etc/arcanum-career/seal-key.pem
  chmod 600 /etc/arcanum-career/seal-key.pem
fi
echo "Public key for CareerSeals.PublicKeys:"
openssl pkey -in /etc/arcanum-career/seal-key.pem -pubout -outform DER | base64 -w0; echo
install -m 644 arcanum-career.service /etc/systemd/system/arcanum-career.service
install -m 644 Caddyfile /etc/caddy/Caddyfile
systemctl daemon-reload
systemctl enable arcanum-career caddy
systemctl reload-or-restart caddy
