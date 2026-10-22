#!/usr/bin/env bash
# Deploy from your machine (repo root):  DEPLOY_HOST=root@<vps-ip> ./deploy/scripts/deploy.sh
# Optional: DEPLOY_KEY=~/.ssh/chewy_vps (SSH key), DEPLOY_RID=linux-x64 (linux-arm64 for ARM servers).
# Builds the API (self-contained: no .NET install needed on the server), the inventory app (served at /inventory/)
# and the landing page (served at /), uploads them as tarballs over SSH (no rsync needed on Windows), and restarts
# the service. Layout matches deploy/nginx/chewypetsfeeds.conf. EF migrations run at API startup.
set -euo pipefail
# Git Bash on Windows rewrites arguments like /inventory/ into Windows paths; keep them literal.
export MSYS_NO_PATHCONV=1
: "${DEPLOY_HOST:?set DEPLOY_HOST=user@host}"
RID=${DEPLOY_RID:-linux-x64}
SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ServerAliveInterval=15 -o ServerAliveCountMax=8)
[ -n "${DEPLOY_KEY:-}" ] && SSH_OPTS+=(-i "$DEPLOY_KEY" -o IdentitiesOnly=yes)
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=$(mktemp -d); trap 'rm -rf "$OUT"' EXIT
# Windows tools (dotnet, node) need a Windows path; with path conversion off, "/tmp/…" would reach MSBuild as a /switch.
command -v cygpath >/dev/null && { OUT=$(cygpath -m "$OUT"); ROOT=$(cygpath -m "$ROOT"); }

echo "== API ($RID) =="
dotnet publish "$ROOT/src/Inventory.Api" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$OUT/api"

echo "== Inventory app (/inventory/) =="
(cd "$ROOT/client/inventory-ui" && npm ci && npx ng build --configuration production --base-href /inventory/ --output-path "$OUT/ui")

echo "== Landing page (/) =="
(cd "$ROOT/client/landing-ui" && npm ci && npx ng build --configuration production --output-path "$OUT/landing")

echo "== Upload =="
# Relative paths from inside $OUT: tar and scp read "C:/…" as a remote host "C".
(cd "$OUT" && tar -czf release.tgz api ui/browser landing/browser)
# A dropped connection mid-upload just retries (up to 4 tries); the size is checked on the server before anything is swapped in.
size=$(cd "$OUT" && wc -c < release.tgz | tr -d ' ')
for try in 1 2 3 4; do
  (cd "$OUT" && scp "${SSH_OPTS[@]}" release.tgz "$DEPLOY_HOST:/tmp/chewy-release.tgz") \
    && [ "$(ssh "${SSH_OPTS[@]}" "$DEPLOY_HOST" 'wc -c < /tmp/chewy-release.tgz' | tr -d ' \r')" = "$size" ] && break
  [ $try = 4 ] && { echo "Upload failed after 4 tries."; exit 1; }
  echo "Upload interrupted — retrying ($try)…"; sleep 5
done

# sudo is a no-op when deploying as root.
ssh "${SSH_OPTS[@]}" "$DEPLOY_HOST" 'set -e; S=$([ "$(id -u)" -eq 0 ] || echo sudo)
  rm -rf /tmp/chewy-release && mkdir -p /tmp/chewy-release && tar -xzf /tmp/chewy-release.tgz -C /tmp/chewy-release
  $S systemctl stop inventory-api || true
  $S rm -rf /opt/inventory/api.new && $S cp -a /tmp/chewy-release/api /opt/inventory/api.new
  $S rm -rf /opt/inventory/api.old && ( [ -d /opt/inventory/api ] && $S mv /opt/inventory/api /opt/inventory/api.old || true )
  $S mv /opt/inventory/api.new /opt/inventory/api
  $S chown -R inventory:inventory /opt/inventory/api && $S chmod +x /opt/inventory/api/Inventory.Api
  $S mkdir -p /var/www/inventory /var/www/landing
  $S find /var/www/inventory /var/www/landing -mindepth 1 -delete
  $S cp -a /tmp/chewy-release/ui/browser/. /var/www/inventory/
  $S cp -a /tmp/chewy-release/landing/browser/. /var/www/landing/
  $S systemctl start inventory-api
  rm -rf /tmp/chewy-release /tmp/chewy-release.tgz
  for i in $(seq 1 45); do curl -fsS http://127.0.0.1:5000/api/health >/dev/null 2>&1 && break; sleep 2; done
  if ! curl -fsS http://127.0.0.1:5000/api/health; then
    echo; echo "API NOT HEALTHY after deploy — last log lines:"; journalctl -u inventory-api --no-pager -n 25 -o cat
    echo "Roll back: systemctl stop inventory-api && mv /opt/inventory/api /opt/inventory/api.bad && mv /opt/inventory/api.old /opt/inventory/api && systemctl start inventory-api"
    exit 1
  fi; echo'
echo "Deployed."
