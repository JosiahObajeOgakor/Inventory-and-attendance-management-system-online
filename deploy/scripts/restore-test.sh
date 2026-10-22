#!/usr/bin/env bash
# Weekly proof that the backups restore (inventory-restore-test.timer, as root). Downloads the newest daily backup of each
# schema FROM R2 (not the local copy), decrypts it with the server's restore key, loads it into a scratch database, compares
# table and row counts with the live one, then drops the scratch database. Alerts on any failure. A backup never restored is a hope.
# Key: RESTORE_AGE_IDENTITY (default /etc/inventory/backup-restore.key, root 600). Disaster recovery uses the OWNER's key instead.
set -uo pipefail
source /etc/inventory/backup.env
export RCLONE_CONFIG_R2_TYPE=s3 RCLONE_CONFIG_R2_PROVIDER=Cloudflare RCLONE_CONFIG_R2_NO_CHECK_BUCKET=true \
       RCLONE_CONFIG_R2_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID" RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY" \
       RCLONE_CONFIG_R2_ENDPOINT="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com"
KEY=${RESTORE_AGE_IDENTITY:-/etc/inventory/backup-restore.key}
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
my() { mysql "$@"; }   # runs as root: Ubuntu's MySQL root logs in over the local socket, and the scratch DB needs CREATE/DROP
status=0; report=()

for schema in $BACKUP_SCHEMAS; do
  newest=$(rclone lsf "r2:$R2_BUCKET/daily/$schema/" --files-only 2>/dev/null | sort | tail -n 1)
  [ -n "$newest" ] || { report+=("FAIL $schema: no backup in R2"); status=1; continue; }
  if ! rclone copyto "r2:$R2_BUCKET/daily/$schema/$newest" "$WORK/dump.age" -q \
     || ! age -d -i "$KEY" "$WORK/dump.age" | gunzip > "$WORK/dump.sql"; then
    report+=("FAIL $schema: could not download/decrypt $newest"); status=1; continue; fi

  scratch="restoretest_${schema}"
  my -e "DROP DATABASE IF EXISTS \`$scratch\`; CREATE DATABASE \`$scratch\`"
  sed "s/\`$schema\`/\`$scratch\`/g" "$WORK/dump.sql" | my || { report+=("FAIL $schema: restore into MySQL failed"); status=1; my -e "DROP DATABASE IF EXISTS \`$scratch\`"; continue; }
  q="SELECT COUNT(*), COALESCE(SUM(table_rows),0) FROM information_schema.tables WHERE table_schema="
  read -r lt lr < <(my -N -e "$q'$schema'")
  read -r rt rr < <(my -N -e "$q'$scratch'")
  my -e "DROP DATABASE \`$scratch\`"
  # Row counts are InnoDB estimates and the backup is up to a day old, so tables must match exactly and rows roughly.
  if [ "$lt" = "$rt" ] && [ "$rt" -gt 0 ]; then report+=("OK   $schema from $newest: $rt tables, ~$rr rows (live ~$lr)")
  else report+=("FAIL $schema: live has $lt tables, restored has $rt"); status=1; fi
done

printf '%s\n' "${report[@]}"
if [ $status -eq 0 ]; then date -u +%s > /var/lib/inventory-backup/last_restore_test
else /usr/local/bin/inventory-alert "Backup restore test FAILED" "$(printf '%s; ' "${report[@]}")" || true; fi
exit $status
