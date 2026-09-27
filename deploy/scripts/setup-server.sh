#!/usr/bin/env bash
# One-time provisioning of a fresh Ubuntu 22.04/24.04 server (x86_64 or arm64). Run as root: bash setup-server.sh
# Idempotent: safe to re-run after changing anything under deploy/. Read deploy/README.md first.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run as root"; exit 1; }
HERE=$(cd "$(dirname "$0")/.." && pwd)
export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y nginx mysql-server certbot python3-certbot-nginx ufw fail2ban unattended-upgrades \
                   age rclone curl openssl unzip jq
# .NET's globalization needs ICU (the API is self-contained but still loads the system libicu).
apt-get install -y libicu-dev || true
timedatectl set-timezone UTC || true      # timers and logs in UTC; the app converts to Lagos time itself

# --- swap: a small VPS can run out of RAM during a spike; 2 GB of swap turns a crash into a slowdown ---
if ! swapon --show | grep -q .; then
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  sysctl -w vm.swappiness=10 >/dev/null && echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf
fi

# --- service accounts (no login shell) ---
id inventory >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin inventory
id inventory-backup >/dev/null 2>&1 || useradd --system --home-dir /var/lib/inventory-backup --create-home --shell /usr/sbin/nologin inventory-backup
install -d -o inventory -g inventory -m 750 /opt/inventory/api /var/log/inventory /var/lib/inventory /var/lib/inventory/archives
install -d -o root -g root -m 755 /var/www/inventory /var/www/landing /var/www/certbot
install -d -o root -g inventory-backup -m 750 /etc/inventory
install -d -o inventory-backup -g inventory-backup -m 750 /var/lib/inventory-backup

# --- MySQL: loopback only + small footprint + binary logs for point-in-time recovery ---
install -m 644 "$HERE/mysql/inventory.cnf" /etc/mysql/mysql.conf.d/inventory.cnf
# About a quarter of RAM for InnoDB's cache (256 MB minimum), leaving room for the API, nginx and the OS.
ram_mb=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo); pool_mb=$(( ram_mb / 4 )); [ "$pool_mb" -ge 256 ] || pool_mb=256
printf '[mysqld]\ninnodb_buffer_pool_size = %dM\n' "$pool_mb" > /etc/mysql/mysql.conf.d/inventory-size.cnf
if [ "$ram_mb" -lt 2048 ]; then
  # Small VPS (< 2 GB): drop MySQL's optional memory users so MySQL + the API + nginx fit in RAM (swap is the safety net).
  cat >> /etc/mysql/mysql.conf.d/inventory-size.cnf <<'EOF'
performance_schema      = OFF
max_connections         = 40
innodb_log_buffer_size  = 8M
tmp_table_size          = 16M
max_heap_table_size     = 16M
table_open_cache        = 400
EOF
  # ...and cap the API's managed heap at 384 MB instead of 512 MB.
  mkdir -p /etc/systemd/system/inventory-api.service.d
  printf '[Service]\nEnvironment=DOTNET_GCHeapHardLimit=0x18000000\n' > /etc/systemd/system/inventory-api.service.d/small-vps.conf
fi
systemctl restart mysql
if ss -ltn | awk '{print $4}' | grep -E ':3306$' | grep -vE '^(127\.0\.0\.1|\[::1\]):3306$' | grep -q .; then
  echo "ERROR: MySQL is reachable from outside; fix bind-address before continuing"; exit 1; fi

# --- firewall: 22, 80, 443 only ---
ufw default deny incoming
ufw default allow outgoing
ufw limit 22/tcp            # rate-limits repeated SSH connection attempts
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
# Oracle's Ubuntu images ship iptables rules that REJECT everything but SSH ahead of ufw; open web ports there too.
if iptables -S INPUT 2>/dev/null | grep -q -- '-j REJECT'; then
  iptables -C INPUT -p tcp --dport 80  -j ACCEPT 2>/dev/null || iptables -I INPUT 1 -p tcp --dport 80  -j ACCEPT
  iptables -C INPUT -p tcp --dport 443 -j ACCEPT 2>/dev/null || iptables -I INPUT 1 -p tcp --dport 443 -j ACCEPT
  command -v netfilter-persistent >/dev/null && netfilter-persistent save || true
fi

# --- SSH: keys only. Refuses to lock the door unless a key is already installed for root, so it can't lock you out. ---
if [ -s /root/.ssh/authorized_keys ]; then
  cat > /etc/ssh/sshd_config.d/10-hardening.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
PubkeyAuthentication yes
MaxAuthTries 4
LoginGraceTime 30
X11Forwarding no
EOF
  sshd -t && (systemctl reload ssh 2>/dev/null || systemctl reload sshd)
else
  echo "WARNING: no /root/.ssh/authorized_keys — leaving SSH password login ON. Add a key, then re-run this script."
fi

# --- fail2ban: bans IPs that keep failing SSH logins or hammering nginx ---
cat > /etc/fail2ban/jail.d/inventory.local <<'EOF'
[DEFAULT]
bantime = 1h
findtime = 10m
maxretry = 5
backend = systemd

[sshd]
enabled = true

[nginx-limit-req]
enabled = true
logpath = /var/log/nginx/error.log
backend = auto
EOF
systemctl enable --now fail2ban && systemctl restart fail2ban

# --- automatic security updates (no automatic reboots; the health check reports if one is pending) ---
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF

# --- keep logs bounded ---
mkdir -p /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=500M\nMaxRetentionSec=30day\n' > /etc/systemd/journald.conf.d/size.conf
systemctl restart systemd-journald

# --- app units + ops scripts ---
for u in inventory-api.service inventory-backup.service inventory-backup.timer inventory-healthcheck.service inventory-healthcheck.timer \
         inventory-restore-test.service inventory-restore-test.timer; do
  install -m 644 "$HERE/systemd/$u" "/etc/systemd/system/$u"
done
install -m 755 "$HERE/scripts/backup-mysql.sh" /usr/local/bin/inventory-backup
install -m 755 "$HERE/scripts/restore-test.sh" /usr/local/bin/inventory-restore-test
install -m 755 "$HERE/scripts/health-check.sh" /usr/local/bin/inventory-health-check
install -m 755 "$HERE/scripts/alert.sh" /usr/local/bin/inventory-alert
systemctl daemon-reload
systemctl enable inventory-api.service inventory-backup.timer inventory-healthcheck.timer inventory-restore-test.timer
systemctl start inventory-backup.timer inventory-healthcheck.timer inventory-restore-test.timer

cat <<'MSG'

Provisioned. Next (see deploy/README.md): MySQL users + /etc/inventory/*.env, then deploy.sh from your machine,
then DNS -> enable-https.sh, then the first admin.
MSG
