#!/usr/bin/env bash
# One-time provisioning of a fresh Ubuntu 22.04/24.04 (arm64) Oracle Cloud VM. Run as root: sudo bash setup-server.sh
# Idempotent where practical. Read deploy/README.md first (DNS, OCI security list, first admin).
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run as root"; exit 1; }
HERE=$(cd "$(dirname "$0")/.." && pwd)

apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y nginx mysql-server certbot python3-certbot-nginx ufw age curl openssl unzip rsync

# --- service account (no login shell, no home) ---
id inventory >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin inventory
id inventory-backup >/dev/null 2>&1 || useradd --system --home-dir /var/lib/inventory-backup --create-home --shell /usr/sbin/nologin inventory-backup
install -d -o inventory -g inventory -m 750 /opt/inventory/api /var/log/inventory
install -d -o root -g root -m 755 /var/www/inventory /var/www/certbot
install -d -o root -g root -m 750 /etc/inventory
install -d -o inventory-backup -g inventory-backup -m 750 /var/lib/inventory-backup

# --- MySQL: loopback only + small footprint ---
install -m 644 "$HERE/mysql/inventory.cnf" /etc/mysql/mysql.conf.d/inventory.cnf
systemctl restart mysql
if ss -ltn | awk '{print $4}' | grep -E ':3306$' | grep -vE '^(127\.0\.0\.1|\[::1\]):3306$' | grep -q .; then
  echo "ERROR: MySQL is reachable from outside; fix bind-address before continuing"; exit 1; fi

# --- firewall: 22, 80, 443 only (also open the same three in the OCI security list / NSG) ---
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
# Oracle's Ubuntu images ship iptables rules that REJECT everything but SSH ahead of ufw; open web ports there too.
iptables -C INPUT -p tcp --dport 80  -j ACCEPT 2>/dev/null || iptables -I INPUT 5 -p tcp --dport 80  -j ACCEPT
iptables -C INPUT -p tcp --dport 443 -j ACCEPT 2>/dev/null || iptables -I INPUT 5 -p tcp --dport 443 -j ACCEPT
command -v netfilter-persistent >/dev/null && netfilter-persistent save || true

# --- app units ---
install -m 644 "$HERE/systemd/inventory-api.service" /etc/systemd/system/inventory-api.service
install -m 644 "$HERE/systemd/inventory-backup.service" /etc/systemd/system/inventory-backup.service
install -m 644 "$HERE/systemd/inventory-backup.timer" /etc/systemd/system/inventory-backup.timer
install -m 644 "$HERE/systemd/inventory-healthcheck.service" /etc/systemd/system/inventory-healthcheck.service
install -m 644 "$HERE/systemd/inventory-healthcheck.timer" /etc/systemd/system/inventory-healthcheck.timer
install -m 755 "$HERE/scripts/backup-mysql.sh" /usr/local/bin/inventory-backup
install -m 755 "$HERE/scripts/restore-test.sh" /usr/local/bin/inventory-restore-test
install -m 755 "$HERE/scripts/health-check.sh" /usr/local/bin/inventory-health-check
systemctl daemon-reload
systemctl enable inventory-api.service inventory-backup.timer inventory-healthcheck.timer

cat <<'MSG'

Provisioned. Remaining manual steps (see deploy/README.md):
  1. Create the MySQL users (app + backup) and put the secrets in /etc/inventory/api.env, backup.cnf, backup.env, monitor.env (all mode 600).
  2. Point DNS at this VM, install deploy/nginx/inventory.conf, run: certbot --nginx -d <domain>
  3. Deploy the API + Angular build (deploy/scripts/deploy.sh from your machine), then create the first admin.
MSG
