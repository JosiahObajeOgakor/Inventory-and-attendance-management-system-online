#!/usr/bin/env bash
# Deploy from your machine (repo root):  DEPLOY_HOST=ubuntu@<vm-ip> ./deploy/scripts/deploy.sh
# Builds the API for linux-arm64 (self-contained: no .NET install needed on the VM) and the Angular app, copies both, and restarts the service.
# EF migrations are applied by the API at startup (Database:MigrateOnStartup), so a deploy needs no separate migration step.
set -euo pipefail
: "${DEPLOY_HOST:?set DEPLOY_HOST=user@host}"
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=$(mktemp -d); trap 'rm -rf "$OUT"' EXIT

echo "== API =="
dotnet publish "$ROOT/src/Inventory.Api" -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=false -o "$OUT/api"

if [ -d "$ROOT/client/inventory-ui" ]; then
  echo "== Angular =="
  (cd "$ROOT/client/inventory-ui" && npm ci && npx ng build --configuration production --output-path "$OUT/ui")
fi

echo "== Upload =="
rsync -az --delete "$OUT/api/" "$DEPLOY_HOST:/tmp/inventory-api/"
[ -d "$OUT/ui" ] && rsync -az --delete "$OUT/ui/browser/" "$DEPLOY_HOST:/tmp/inventory-ui/" || true
ssh "$DEPLOY_HOST" 'sudo rsync -a --delete /tmp/inventory-api/ /opt/inventory/api/ \
  && sudo chown -R inventory:inventory /opt/inventory/api && sudo chmod +x /opt/inventory/api/Inventory.Api \
  && ( [ -d /tmp/inventory-ui ] && sudo rsync -a --delete /tmp/inventory-ui/ /var/www/inventory/ || true ) \
  && sudo systemctl restart inventory-api && sleep 3 && curl -fsS http://127.0.0.1:5000/api/health && echo'
echo "Deployed."
