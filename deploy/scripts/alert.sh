#!/usr/bin/env bash
# Sends an operations alert by email (SMTP) and WhatsApp. Used by the health check, backup and restore test.
#   inventory-alert "Subject" "Message text"
# Config /etc/inventory/alert.env (root:inventory-backup, 640) — only what alerting needs, never the full app secrets:
#   ALERT_EMAIL_TO=you@example.com
#   SMTP_URL=smtps://smtppro.zoho.com:465  SMTP_USER=...  SMTP_PASS=...  SMTP_FROM=...
#   WA_TOKEN=...  WA_PHONE_NUMBER_ID=...  WA_TO=2349150464707
#   WA_TEMPLATE=ops_alert   # optional: an approved template with ONE body variable {{1}}. Without it a plain text is sent,
#                            # which WhatsApp only delivers if the admin messaged the business number in the last 24 hours.
#   HEARTBEAT_URL=https://hc-ping.com/<uuid>   # optional: an external "dead man's switch" (see health-check.sh)
set -uo pipefail
subject=${1:-Alert}; message=${2:-}
source /etc/inventory/alert.env 2>/dev/null || { echo "alert: /etc/inventory/alert.env missing" >&2; exit 1; }
host=$(hostname)
sent=0

if [ -n "${ALERT_EMAIL_TO:-}" ] && [ -n "${SMTP_URL:-}" ]; then
  mail=$(mktemp); trap 'rm -f "$mail"' EXIT
  printf 'From: %s\r\nTo: %s\r\nSubject: [%s] %s\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n%s\r\n' \
    "${SMTP_FROM:-$SMTP_USER}" "$ALERT_EMAIL_TO" "$host" "$subject" "$message" > "$mail"
  curl -fsS -m 30 --ssl-reqd --url "$SMTP_URL" --user "$SMTP_USER:$SMTP_PASS" \
       --mail-from "${SMTP_FROM:-$SMTP_USER}" --mail-rcpt "$ALERT_EMAIL_TO" -T "$mail" >/dev/null 2>&1 && sent=$((sent+1)) \
    || echo "alert: email failed" >&2
fi

if [ -n "${WA_TOKEN:-}" ] && [ -n "${WA_PHONE_NUMBER_ID:-}" ] && [ -n "${WA_TO:-}" ]; then
  text="$subject: $message"; text=${text:0:900}
  esc=$(printf '%s' "$text" | sed 's/\\/\\\\/g; s/"/\\"/g' | tr '\n\r\t' '   ')
  if [ -n "${WA_TEMPLATE:-}" ]; then
    body="{\"messaging_product\":\"whatsapp\",\"to\":\"$WA_TO\",\"type\":\"template\",\"template\":{\"name\":\"$WA_TEMPLATE\",\"language\":{\"code\":\"en\"},\"components\":[{\"type\":\"body\",\"parameters\":[{\"type\":\"text\",\"text\":\"$esc\"}]}]}}"
  else
    body="{\"messaging_product\":\"whatsapp\",\"to\":\"$WA_TO\",\"type\":\"text\",\"text\":{\"body\":\"$esc\"}}"
  fi
  curl -fsS -m 20 -H "Authorization: Bearer $WA_TOKEN" -H "Content-Type: application/json" \
       -d "$body" "https://graph.facebook.com/v21.0/$WA_PHONE_NUMBER_ID/messages" >/dev/null 2>&1 && sent=$((sent+1)) \
    || echo "alert: WhatsApp failed" >&2
fi

[ $sent -gt 0 ] || { echo "alert: NO channel delivered: $subject: $message" >&2; exit 1; }
