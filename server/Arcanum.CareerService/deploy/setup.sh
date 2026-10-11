#!/bin/sh
# First-time setup of the career service's machine (Ubuntu 24.04): run there with sudo, next to the Caddyfile and the unit.
set -eu
apt-get update
apt-get install -y aspnetcore-runtime-8.0 caddy
id arcanum >/dev/null 2>&1 || useradd --system --home /var/lib/arcanum-career --shell /usr/sbin/nologin arcanum
install -d -o arcanum -g arcanum /opt/arcanum-career
install -d -m 700 /etc/arcanum-career
[ -f /etc/arcanum-career/secrets.env ] || install -m 600 /dev/null /etc/arcanum-career/secrets.env
install -m 644 arcanum-career.service /etc/systemd/system/arcanum-career.service
install -m 644 Caddyfile /etc/caddy/Caddyfile
systemctl daemon-reload
systemctl enable arcanum-career caddy
systemctl reload-or-restart caddy
