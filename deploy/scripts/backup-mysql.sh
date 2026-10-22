#!/usr/bin/env bash
# Nightly encrypted MySQL backup -> Cloudflare R2 (OFF the server). Retention: 14 daily + 8 weekly + 12 monthly per schema.
# Run by inventory-backup.timer as the `inventory-backup` user. Config /etc/inventory/backup.env (root:inventory-backup, 640):
#   BACKUP_SCHEMAS="inventory_identity inventory_chewypets inventory_candid"
#   BACKUP_AGE_RECIPIENTS="age1...owner age1...server"   # PUBLIC keys. The owner's private key stays OFF the server (disaster
#                                                         # recovery); the server's own key only lets the weekly restore test run.
#   R2_ACCOUNT_ID=...  R2_ACCESS_KEY_ID=...  R2_SECRET_ACCESS_KEY=...  R2_BUCKET=chewypets-backups
# MySQL credentials: /etc/inventory/backup.cnf ([client] user/password; SELECT, LOCK TABLES, SHOW VIEW, TRIGGER, EVENT only).
set -euo pipefail
source /etc/inventory/backup.env
export RCLONE_CONFIG_R2_TYPE=s3 RCLONE_CONFIG_R2_PROVIDER=Cloudflare RCLONE_CONFIG_R2_NO_CHECK_BUCKET=true \
       RCLONE_CONFIG_R2_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID" RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY" \
       RCLONE_CONFIG_R2_ENDPOINT="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com"
REMOTE="r2:$R2_BUCKET"
STATE_DIR=/var/lib/inventory-backup
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
WORK=$(mktemp -d "$STATE_DIR/work.XXXXXX"); trap 'rm -rf "$WORK"' EXIT

fail() { echo "BACKUP FAILED: $*" >&2; /usr/local/bin/inventory-alert "Backup FAILED" "$* — the nightly database backup did not complete. Check: journalctl -u inventory-backup" || true; exit 1; }
recips=(); for r in $BACKUP_AGE_RECIPIENTS; do recips+=(-r "$r"); done

total=0
for schema in $BACKUP_SCHEMAS; do
  out="$WORK/${schema}_${STAMP}.sql.gz.age"
  # --single-transaction: a consistent snapshot without blocking sales (InnoDB).
  mysqldump --defaults-extra-file=/etc/inventory/backup.cnf \
      --single-transaction --routines --triggers --events --no-tablespaces --set-gtid-purged=OFF --databases "$schema" \
    | gzip -9 | age "${recips[@]}" > "$out" || fail "dump of $schema"
  [ "$(stat -c %s "$out")" -gt 500 ] || fail "dump of $schema is suspiciously small"
  total=$((total + $(stat -c %s "$out")))

  rclone copyto "$out" "$REMOTE/daily/$schema/$(basename "$out")" --s3-no-check-bucket -q || fail "upload of $schema"
  [ "$(date -u +%u)" = 7 ] && { rclone copyto "$out" "$REMOTE/weekly/$schema/$(basename "$out")" -q || fail "weekly upload of $schema"; }
  [ "$(date -u +%d)" = 01 ] && { rclone copyto "$out" "$REMOTE/monthly/$schema/$(basename "$out")" -q || fail "monthly upload of $schema"; }
  # Verify the object really landed with the same size.
  remote_size=$(rclone size --json "$REMOTE/daily/$schema/$(basename "$out")" 2>/dev/null | sed -E 's/.*"bytes":([0-9]+).*/\1/')
  [ "$remote_size" = "$(stat -c %s "$out")" ] || fail "uploaded size mismatch for $schema"
  cp "$out" "$STATE_DIR/latest_${schema}.sql.gz.age"   # a local convenience copy; the off-server one is the real backup
done

prune() {  # prefix keep — names carry a sortable UTC timestamp
  rclone lsf "$REMOTE/$1" --files-only 2>/dev/null | sort | head -n "-$2" | while read -r name; do rclone deletefile "$REMOTE/$1$name" -q; done
}
for schema in $BACKUP_SCHEMAS; do prune "daily/$schema/" 14; prune "weekly/$schema/" 8; prune "monthly/$schema/" 12; done

date -u +%s > "$STATE_DIR/last_success"      # read by health-check.sh
echo "Backup OK $STAMP ($((total / 1024)) KB encrypted)"
