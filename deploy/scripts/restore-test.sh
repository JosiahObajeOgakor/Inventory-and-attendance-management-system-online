#!/usr/bin/env bash
# Weekly proof that backups are restorable: download the newest daily dump of each schema, restore it into a scratch
# database, compare table counts with the live database, drop the scratch database. A backup you have never restored is a hope.
# Needs the age PRIVATE key at $RESTORE_AGE_IDENTITY (keep it off the server normally; copy it over only for this test, or
# run this script from an admin machine that can reach MySQL through an SSH tunnel).
set -euo pipefail
source /etc/inventory/backup.env
: "${RESTORE_AGE_IDENTITY:?path to the age private key}"
WORK=$(mktemp -d); trap 'rm -rf "$WORK"; ' EXIT
status=0

for schema in $BACKUP_SCHEMAS; do
  newest=$(oci os object list --namespace "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET" --prefix "daily/${schema}/" --all --query 'data[*].name' --raw-output \
           | tr -d '[]", ' | grep -v '^$' | sort | tail -n 1)
  [ -n "$newest" ] || { echo "no backup found for $schema"; status=1; continue; }
  oci os object get --namespace "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET" --name "$newest" --file "$WORK/dump.age" >/dev/null
  age -d -i "$RESTORE_AGE_IDENTITY" "$WORK/dump.age" | gunzip > "$WORK/dump.sql"

  scratch="restoretest_${schema}"
  mysql --defaults-extra-file=/etc/inventory/backup.cnf -e "DROP DATABASE IF EXISTS \`$scratch\`; CREATE DATABASE \`$scratch\`"
  sed "s/\`$schema\`/\`$scratch\`/g" "$WORK/dump.sql" | mysql --defaults-extra-file=/etc/inventory/backup.cnf
  live=$(mysql --defaults-extra-file=/etc/inventory/backup.cnf -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$schema'")
  rest=$(mysql --defaults-extra-file=/etc/inventory/backup.cnf -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$scratch'")
  mysql --defaults-extra-file=/etc/inventory/backup.cnf -e "DROP DATABASE \`$scratch\`"
  if [ "$live" = "$rest" ] && [ "$rest" -gt 0 ]; then echo "OK   $schema restored from $newest ($rest tables)"; else echo "FAIL $schema: live=$live restored=$rest"; status=1; fi
done
[ $status -eq 0 ] && date -u +%s > /var/lib/inventory-backup/last_restore_test
exit $status
