#!/usr/bin/env bash
# Daily encrypted MySQL backup -> Oracle Object Storage (OFF the VM), with retention 7 daily + 4 weekly.
# Run by inventory-backup.timer as the `inventory-backup` user. Config: /etc/inventory/backup.env (root-owned, 600):
#   BACKUP_SCHEMAS="inventory_identity inventory_chewypets inventory_candid"
#   BACKUP_AGE_RECIPIENT="age1..."          # PUBLIC key; the private key is kept OFF the server
#   OCI_BUCKET="inventory-backups"          # Always Free Object Storage bucket (20 GB)
#   OCI_NAMESPACE="<tenancy object storage namespace>"
# MySQL credentials are read from /etc/inventory/backup.cnf ([client] user/password; user has SELECT, LOCK TABLES, SHOW VIEW, TRIGGER, EVENT only).
set -euo pipefail

source /etc/inventory/backup.env
STATE_DIR=/var/lib/inventory-backup
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
DOW=$(date -u +%u)                       # 7 = Sunday -> also keep as a weekly copy
WORK=$(mktemp -d "$STATE_DIR/work.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$STATE_DIR"

fail() { echo "BACKUP FAILED: $*" >&2; exit 1; }

for schema in $BACKUP_SCHEMAS; do
  out="$WORK/${schema}_${STAMP}.sql.gz.age"
  # --single-transaction gives a consistent snapshot without blocking the app (InnoDB only).
  mysqldump --defaults-extra-file=/etc/inventory/backup.cnf \
      --single-transaction --routines --triggers --events --no-tablespaces --set-gtid-purged=OFF \
      --databases "$schema" \
    | gzip -9 \
    | age -r "$BACKUP_AGE_RECIPIENT" > "$out" || fail "dump of $schema"
  [ -s "$out" ] || fail "empty dump for $schema"

  oci os object put --namespace "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET" \
      --name "daily/${schema}/${schema}_${STAMP}.sql.gz.age" --file "$out" --force >/dev/null || fail "upload of $schema"
  if [ "$DOW" = "7" ]; then
    oci os object put --namespace "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET" \
        --name "weekly/${schema}/${schema}_${STAMP}.sql.gz.age" --file "$out" --force >/dev/null || fail "weekly upload of $schema"
  fi
  # Keep the newest local copy only as a convenience; the off-VM copy is the real one.
  cp "$out" "$STATE_DIR/latest_${schema}.sql.gz.age"
done

# Retention: 7 daily, 4 weekly (per schema). Objects sort by timestamped name.
prune() {  # prefix keep
  local prefix=$1 keep=$2
  oci os object list --namespace "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET" --prefix "$prefix" --all --query 'data[*].name' --raw-output 2>/dev/null \
    | tr -d '[]", ' | grep -v '^$' | sort | head -n "-$keep" \
    | while read -r name; do
        oci os object delete --namespace "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET" --object-name "$name" --force >/dev/null
      done
}
for schema in $BACKUP_SCHEMAS; do
  prune "daily/${schema}/" 7
  prune "weekly/${schema}/" 4
done

date -u +%s > "$STATE_DIR/last_success"      # read by health-check.sh
echo "Backup OK $STAMP"
