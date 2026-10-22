#!/usr/bin/env bash
# Get a Let's Encrypt certificate and switch nginx to the full HTTPS site. Run as root on the server, AFTER the domain's
# DNS A records point at this server:   DOMAIN=chewypetsfeeds.com EMAIL=you@example.com bash enable-https.sh
# nginx can't load the HTTPS config before the certificate exists, so this first serves a temporary HTTP-only site that
# answers Let's Encrypt's challenge, then installs deploy/nginx/chewypetsfeeds.conf. Safe to re-run (renewals are automatic).
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run as root"; exit 1; }
: "${DOMAIN:?set DOMAIN=chewypetsfeeds.com}"; : "${EMAIL:?set EMAIL=you@example.com (certificate expiry notices)}"
HERE=$(cd "$(dirname "$0")/.." && pwd)
SITE=/etc/nginx/sites-available/chewypetsfeeds.conf

rm -f /etc/nginx/sites-enabled/default
if [ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]; then
  cat > "$SITE" <<EOF
server {
    listen 80; listen [::]:80;
    server_name $DOMAIN www.$DOMAIN;
    location /.well-known/acme-challenge/ { root /var/www/certbot; }
    location / { root /var/www/landing; try_files \$uri /index.html; }
}
EOF
  ln -sf "$SITE" /etc/nginx/sites-enabled/chewypetsfeeds.conf
  nginx -t && systemctl reload nginx
  certbot certonly --webroot -w /var/www/certbot -d "$DOMAIN" -d "www.$DOMAIN" \
    --non-interactive --agree-tos -m "$EMAIL" --deploy-hook "systemctl reload nginx"
fi

cp -f "$SITE" "$SITE.bak" 2>/dev/null || true
sed "s/chewypetsfeeds\.com/$DOMAIN/g" "$HERE/nginx/chewypetsfeeds.conf" > "$SITE"
ln -sf "$SITE" /etc/nginx/sites-enabled/chewypetsfeeds.conf
if ! nginx -t; then
  echo "ERROR: the HTTPS config was rejected; restoring the previous one."; mv -f "$SITE.bak" "$SITE" 2>/dev/null; nginx -t && systemctl reload nginx; exit 1
fi
systemctl reload nginx
echo "HTTPS is on: https://$DOMAIN"
