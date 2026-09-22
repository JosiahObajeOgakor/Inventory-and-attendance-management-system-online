# Deployment runbook — Oracle Cloud Always Free (Ubuntu, arm64)

```
Internet ─▶ :443 NGINX ─┬─▶ /       Angular static files   (/var/www/inventory)
                        └─▶ /api/*  Kestrel 127.0.0.1:5000 (systemd: inventory-api, user `inventory`)
                                        └─▶ MySQL 127.0.0.1:3306  (never public)
```

> **Status: written but NOT yet run on a real VM.** The scripts have only been syntax-checked. The first deployment
> should be a rehearsal on a scratch VM (Oracle A1 capacity in your home region is sometimes unavailable — check the
> quota in the console before committing; Always Free instances that sit idle can also be reclaimed, so keep it in use).

## 1. Oracle Cloud
1. Create a `VM.Standard.A1.Flex` instance (2 OCPU / 12 GB), Ubuntu 22.04 or 24.04 **aarch64**, with your SSH key.
2. **Security list / NSG ingress:** TCP 22 (restrict to your IP), 80, 443. **Nothing else. Never 3306.**
3. Reserve a public IP. In Namecheap, add an **A record** `inventory` → that IP.
4. Create an Object Storage bucket `inventory-backups` (Always Free 20 GB). Create an API key for a user whose policy allows only that bucket.

## 2. Provision (once)
```bash
scp -r deploy ubuntu@<ip>:/tmp/ && ssh ubuntu@<ip> 'sudo bash /tmp/deploy/scripts/setup-server.sh'
```
This installs NGINX/MySQL/certbot/ufw/age, creates the `inventory` and `inventory-backup` service users, binds MySQL to loopback
(and **aborts if MySQL is reachable from outside**), opens only 22/80/443, and installs the systemd units.

## 3. Secrets (never in git)
```sql
-- mysql -u root
CREATE USER 'inventory_app'@'localhost'    IDENTIFIED BY '<strong-random>';
-- The API creates/migrates the schemas at startup, so it needs DDL on inventory_* only (not on mysql.* or anything else):
GRANT ALL ON `inventory\_%`.* TO 'inventory_app'@'localhost';
CREATE USER 'inventory_backup'@'localhost' IDENTIFIED BY '<another-strong-random>';
GRANT SELECT, LOCK TABLES, SHOW VIEW, TRIGGER, EVENT ON `inventory\_%`.* TO 'inventory_backup'@'localhost';
-- restore-test.sh also needs: GRANT ALL ON `restoretest\_%`.* TO 'inventory_backup'@'localhost';
```
Create these root-owned, mode 600 files under `/etc/inventory/`:

| File | Contents |
|---|---|
| `api.env` | see `src/Inventory.Api/.env.example` (`ConnectionStrings__Default=Server=127.0.0.1;User=inventory_app;Password=…;`) |
| `backup.cnf` | `[client]` `user=inventory_backup` `password=…` |
| `backup.env` | `BACKUP_SCHEMAS`, `BACKUP_AGE_RECIPIENT` (age **public** key — keep the private key on your own machine), `OCI_BUCKET`, `OCI_NAMESPACE` |
| `monitor.env` | `DOMAIN`, `ALERT_WEBHOOK` (e.g. a free healthchecks.io ping URL), `ALERT_EMAIL` |

Generate the age key pair on **your** machine: `age-keygen -o inventory-backup.key` (store it in a password manager; without it backups cannot be decrypted).

## 4. HTTPS
```bash
sudo cp deploy/nginx/inventory.conf /etc/nginx/sites-available/inventory.conf   # edit the domain first
sudo ln -s /etc/nginx/sites-available/inventory.conf /etc/nginx/sites-enabled/ && sudo rm -f /etc/nginx/sites-enabled/default
sudo certbot --nginx -d inventory.<your-domain> --redirect -m you@example.com --agree-tos
sudo nginx -t && sudo systemctl reload nginx
```
Let's Encrypt renews automatically (`systemctl list-timers | grep certbot`); `health-check.sh` alerts if the certificate has < 14 days left.

## 5. Deploy the application
```bash
DEPLOY_HOST=ubuntu@<ip> ./deploy/scripts/deploy.sh      # from your machine, repo root
```
The API applies EF migrations at startup. Restarts are automatic (`Restart=always`); the service is enabled at boot.

## 6. First administrator
1. Add `Setup__Token=<random>` to `/etc/inventory/api.env`, `sudo systemctl restart inventory-api`.
2. `curl -X POST https://inventory.<domain>/api/setup/first-admin -H 'Content-Type: application/json' -H 'X-Requested-With: inventory-ui' -d '{"setupToken":"…","fullName":"…","username":"…","password":"…"}'`
3. **Remove `Setup__Token`** and restart. The endpoint also refuses to work once any admin exists.

(If you migrate the existing accounts instead, users keep their current passwords; accounts still holding the desktop placeholder hash need a password issued by an admin.)

## 7. Migrating the existing data
Run `tools/Inventory.MigrationTool` from a machine that can reach the restored SQL Server **copy** and the MySQL (through an SSH tunnel to 127.0.0.1:3306 — never expose it). See `docs/MIGRATION_PLAN.md`. Do not point it at the client's live database; restore the `.bak` to a copy first.

## 8. Operations
| Task | Command |
|---|---|
| Logs | `journalctl -u inventory-api -f` |
| Status | `systemctl status inventory-api nginx mysql` · `systemctl list-timers` |
| Backup now | `sudo systemctl start inventory-backup` |
| Prove restorability (weekly) | `sudo RESTORE_AGE_IDENTITY=/path/inventory-backup.key inventory-restore-test` |
| Restart | `sudo systemctl restart inventory-api` |

**Restore drill:** download the newest `daily/<schema>/*.sql.gz.age` from the bucket, `age -d -i inventory-backup.key file | gunzip | mysql -u root`.
Between dumps, MySQL binary logs (7 days) allow point-in-time recovery.

## 9. Acceptance checklist
- [ ] `https://inventory.<domain>` loads, HTTP redirects to HTTPS, certificate valid
- [ ] `nmap -p 3306 <ip>` from outside shows filtered/closed; `ss -ltn` shows 3306 on 127.0.0.1 only
- [ ] Admin and Clerk can both sign in remotely; Clerk gets 403 on admin endpoints
- [ ] `sudo reboot` → site and API come back with no manual steps
- [ ] `inventory-backup` produced an object in the bucket; `inventory-restore-test` passes
- [ ] health-check alert fires when `inventory-api` is stopped (test it)
- [ ] no secrets in `git log -p` / `git grep -i password`
