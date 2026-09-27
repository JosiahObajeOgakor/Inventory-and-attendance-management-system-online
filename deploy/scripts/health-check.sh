#!/usr/bin/env bash
# Every 5 minutes (inventory-healthcheck.timer, as inventory-backup). Checks the services, the API, the public site, disk, memory,
# the TLS certificate, backup and restore-test freshness, the WhatsApp inbox, and that MySQL is never exposed.
# On a problem: alert (email + WhatsApp, via inventory-alert), repeated hourly while it lasts, and an all-clear when it's fixed.
# Config: /etc/inventory/monitor.env  DOMAIN=chewypetsfeeds.com  INBOX_SCHEMAS="inventory_chewypets inventory_candid"
#         (+ /etc/inventory/alert.env HEARTBEAT_URL: pinged when healthy, so an external service notices if this server goes silent)
set -uo pipefail
source /etc/inventory/monitor.env
source /etc/inventory/alert.env 2>/dev/null || true
STATE=/var/lib/inventory-backup
problems=()

for svc in mysql inventory-api nginx; do systemctl is-active --quiet "$svc" || problems+=("service $svc is not running"); done

body=$(curl -fsS -m 10 http://127.0.0.1:5000/api/health 2>/dev/null || true)
[ "$body" = '{"status":"healthy"}' ] || problems+=("API health check failed")

if [ -n "${DOMAIN:-}" ]; then
  code=$(curl -s -o /dev/null -w '%{http_code}' -m 15 "https://$DOMAIN/" || true)
  [ "$code" = 200 ] || problems+=("public site https://$DOMAIN answered $code")
  end=$(echo | openssl s_client -servername "$DOMAIN" -connect "$DOMAIN:443" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2)
  if [ -n "$end" ]; then days=$(( ( $(date -d "$end" +%s) - $(date +%s) ) / 86400 )); [ "$days" -gt 14 ] || problems+=("TLS certificate expires in $days day(s)")
  else problems+=("could not read the TLS certificate for $DOMAIN"); fi
fi

disk=$(df --output=pcent / | tail -1 | tr -dc '0-9');    [ "${disk:-0}" -lt 85 ] || problems+=("disk ${disk}% full")
avail_kb=$(awk '/MemAvailable/ {print $2}' /proc/meminfo); [ "${avail_kb:-0}" -gt 300000 ] || problems+=("low memory: $((avail_kb/1024)) MB free")

if [ -f "$STATE/last_success" ]; then a=$(( $(date +%s) - $(cat "$STATE/last_success") )); [ "$a" -lt 93600 ] || problems+=("last good backup is $((a/3600)) h old")
else problems+=("no successful backup yet"); fi
if [ -f "$STATE/last_restore_test" ]; then a=$(( $(date +%s) - $(cat "$STATE/last_restore_test") )); [ "$a" -lt 777600 ] || problems+=("last good restore test is $((a/86400)) days old")
fi

# WhatsApp inbox: customers waiting > 10 min, or messages that failed for good in the last hour.
for s in ${INBOX_SCHEMAS:-}; do
  read -r stuck failed < <(mysql --defaults-extra-file=/etc/inventory/backup.cnf -N -e \
    "SELECT SUM(Status='Pending' AND ReceivedAt < UTC_TIMESTAMP() - INTERVAL 10 MINUTE), SUM(Status='Failed' AND ProcessedAt > UTC_TIMESTAMP() - INTERVAL 1 HOUR) FROM \`$s\`.inbound_messages" 2>/dev/null || echo "? ?")
  [ "${stuck:-0}" = "?" ] && { problems+=("cannot read the WhatsApp inbox in $s"); continue; }
  [ "${stuck:-0}" = NULL ] && stuck=0; [ "${failed:-0}" = NULL ] && failed=0
  [ "${stuck:-0}" -eq 0 ] || problems+=("$stuck WhatsApp message(s) waiting over 10 min in $s")
  [ "${failed:-0}" -eq 0 ] || problems+=("$failed WhatsApp message(s) could not be answered in the last hour in $s")
done

if ss -ltn 2>/dev/null | awk '{print $4}' | grep -E '(^|:)3306$' | grep -vE '^(127\.0\.0\.1|\[::1\]):3306$' | grep -q .; then problems+=("MySQL is listening on a public address!"); fi

if [ ${#problems[@]} -eq 0 ]; then
  [ -n "${HEARTBEAT_URL:-}" ] && curl -fsS -m 10 "$HEARTBEAT_URL" >/dev/null 2>&1
  if [ -f "$STATE/alert_sent" ]; then rm -f "$STATE/alert_sent"; /usr/local/bin/inventory-alert "Resolved" "All checks are passing again."; fi
  exit 0
fi

msg=$(printf '%s; ' "${problems[@]}")
echo "$msg" >&2
if [ ! -f "$STATE/alert_sent" ] || [ -n "$(find "$STATE/alert_sent" -mmin +60 2>/dev/null)" ]; then
  /usr/local/bin/inventory-alert "Server problem" "$msg" && touch "$STATE/alert_sent"
fi
exit 1
