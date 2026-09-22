#!/usr/bin/env bash
# Runs every 5 minutes (inventory-healthcheck.timer). Checks: services, API health, disk, RAM, TLS certificate expiry,
# backup freshness. On any problem it sends ONE alert (webhook and/or email) per failure set, and re-alerts hourly.
# Config /etc/inventory/monitor.env:  DOMAIN=inventory.example.com  ALERT_WEBHOOK=https://hc-ping.com/<uuid>/fail  ALERT_EMAIL=you@example.com
set -uo pipefail
source /etc/inventory/monitor.env
STATE=/var/lib/inventory-backup
problems=()

for svc in mysql inventory-api nginx; do
  systemctl is-active --quiet "$svc" || problems+=("service $svc is not running")
done

body=$(curl -fsS -m 10 http://127.0.0.1:5000/api/health 2>/dev/null || true)
[ "$body" = '{"status":"healthy"}' ] || problems+=("API health endpoint not healthy")

disk=$(df --output=pcent / | tail -1 | tr -dc '0-9');       [ "${disk:-0}" -lt 85 ] || problems+=("disk ${disk}% full")
avail_kb=$(awk '/MemAvailable/ {print $2}' /proc/meminfo);    [ "${avail_kb:-0}" -gt 400000 ] || problems+=("low memory: $((avail_kb/1024)) MB available")

# TLS certificate days left (needs the public 443 to answer; skipped if DOMAIN unset)
if [ -n "${DOMAIN:-}" ]; then
  end=$(echo | openssl s_client -servername "$DOMAIN" -connect "$DOMAIN:443" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2)
  if [ -n "$end" ]; then days=$(( ( $(date -d "$end" +%s) - $(date +%s) ) / 86400 )); [ "$days" -gt 14 ] || problems+=("TLS certificate expires in $days day(s)")
  else problems+=("could not read TLS certificate for $DOMAIN"); fi
fi

# Last successful backup must be < 26 h old
if [ -f "$STATE/last_success" ]; then age=$(( $(date +%s) - $(cat "$STATE/last_success") )); [ "$age" -lt 93600 ] || problems+=("last successful backup is $((age/3600)) h old")
else problems+=("no successful backup recorded yet"); fi

# Never expose MySQL: 3306 must be listening on loopback only.
if ss -ltn 2>/dev/null | awk '{print $4}' | grep -E '(^|:)3306$' | grep -vE '^(127\.0\.0\.1|\[::1\]):3306$' | grep -q .; then problems+=("MySQL is listening on a non-loopback address!"); fi

if [ ${#problems[@]} -eq 0 ]; then rm -f "$STATE/alert_sent"; exit 0; fi

msg="Inventory server $(hostname): ${problems[*]}"
echo "$msg" >&2
if [ ! -f "$STATE/alert_sent" ] || [ -n "$(find "$STATE/alert_sent" -mmin +60 2>/dev/null)" ]; then
  [ -n "${ALERT_WEBHOOK:-}" ] && curl -fsS -m 10 --data-urlencode "msg=$msg" "$ALERT_WEBHOOK" >/dev/null 2>&1
  [ -n "${ALERT_EMAIL:-}" ] && command -v mail >/dev/null && echo "$msg" | mail -s "Inventory alert" "$ALERT_EMAIL"
  touch "$STATE/alert_sent"
fi
exit 1
