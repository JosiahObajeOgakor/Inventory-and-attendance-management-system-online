# Deployment runbook — chewypetsfeeds.com (Namecheap VPS, Ubuntu x86_64; DNS at Hostinger)

```
Internet ─▶ :443 nginx ─┬─▶ /             landing page      (/var/www/landing)
                        ├─▶ /inventory/   inventory app     (/var/www/inventory)
                        └─▶ /api/*        Kestrel 127.0.0.1:5000 (systemd: inventory-api, user `inventory`)
                                             ├─▶ MySQL 127.0.0.1:3306  (never public)
                                             └─▶ background: WhatsApp inbox worker, payment reconciler
Nightly: encrypted mysqldump ─▶ Cloudflare R2   ·  Weekly: automatic restore test  ·  Every 5 min: health check ─▶ email + WhatsApp
```

## 1. DNS (Hostinger → Domains → chewypetsfeeds.com → DNS records)
`A @ → <VPS IP>` and `A www → <VPS IP>`, TTL 300. Remove any other A/AAAA/CNAME for `@`/`www` (parking). Leave MX/TXT (email).

## 2. Provision (once; re-runnable)
```bash
scp -r deploy root@<ip>:/root/ && ssh root@<ip> 'bash /root/deploy/scripts/setup-server.sh'
```
Installs nginx, MySQL (loopback only, memory sized to the VPS, binlogs 7 days), certbot, rclone, age, a 2 GB swap file;
firewall 22/80/443 only (SSH rate-limited); **SSH keys only** (only if a key is already installed — it won't lock you out);
fail2ban (SSH + nginx); automatic security updates; bounded logs; the systemd units and ops scripts.

## 3. Secrets (never in git) — `/etc/inventory/`
| File | Owner / mode | Contents |
|---|---|---|
| `api.env` | root 600 | everything in `scripts/secrets.env.example`, with `ConnectionStrings__Default=Server=127.0.0.1;User=inventory_app;Password=…;` |
| `backup.cnf` | root:inventory-backup 640 | `[client]` `user=inventory_backup` `password=…` |
| `backup.env` | root:inventory-backup 640 | `BACKUP_SCHEMAS`, `BACKUP_AGE_RECIPIENTS` (owner + server public keys), `R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET` |
| `backup-restore.key` | root 600 | the server's age private key (weekly restore test only) |
| `alert.env` | root:inventory-backup 640 | `ALERT_EMAIL_TO`, `SMTP_URL/USER/PASS/FROM`, `WA_TOKEN`, `WA_PHONE_NUMBER_ID`, `WA_TO`, optional `WA_TEMPLATE`, `HEARTBEAT_URL` |
| `monitor.env` | root:inventory-backup 640 | `DOMAIN=chewypetsfeeds.com`, `INBOX_SCHEMAS="inventory_chewypets inventory_candid"` |

MySQL users:
```sql
-- skip-name-resolve is on, so the API's TCP connection arrives as '127.0.0.1', not 'localhost': create that one.
CREATE USER 'inventory_app'@'127.0.0.1' IDENTIFIED BY '<strong-random>';
GRANT ALL ON `inventory\_%`.* TO 'inventory_app'@'127.0.0.1';          -- the API creates/migrates its schemas at startup
CREATE USER 'inventory_backup'@'localhost' IDENTIFIED BY '<another-strong-random>';
GRANT SELECT, LOCK TABLES, SHOW VIEW, TRIGGER, EVENT ON `inventory\_%`.* TO 'inventory_backup'@'localhost';
GRANT PROCESS ON *.* TO 'inventory_backup'@'localhost';                  -- mysqldump needs it (global-only privilege)
```
**Backup keys:** two age key pairs. The **owner key**'s private half is kept OFF the server (password manager) — it is what
disaster recovery uses. The **server key** lets the weekly restore test prove the backups decrypt. Backups are encrypted to both.

## 4. Deploy (from your machine, repo root)
```bash
DEPLOY_HOST=root@<ip> DEPLOY_KEY=~/.ssh/chewy_vps ./deploy/scripts/deploy.sh
```
Builds the API (self-contained linux-x64), the inventory app (`/inventory/`) and the landing page, uploads over SSH, swaps the
API in, restarts it and checks `/api/health`. The previous API build is kept at `/opt/inventory/api.old` for a quick rollback:
`systemctl stop inventory-api && mv /opt/inventory/api /opt/inventory/api.bad && mv /opt/inventory/api.old /opt/inventory/api && systemctl start inventory-api`.
Migrations run at API startup. Take a backup first for risky releases: `systemctl start inventory-backup`.

## 5. HTTPS (after DNS points here)
```bash
ssh root@<ip> 'DOMAIN=chewypetsfeeds.com EMAIL=<you> bash /root/deploy/scripts/enable-https.sh'
```
Renewal is automatic (`systemctl list-timers | grep certbot`); the health check alerts when < 14 days remain.

## 6. First administrator
Add `Setup__Token=<random>` to `api.env`, restart, then:
`curl -X POST https://chewypetsfeeds.com/api/setup/first-admin -H 'Content-Type: application/json' -H 'X-Requested-With: inventory-ui' -d '{"setupToken":"…","fullName":"…","username":"…","password":"…"}'`
**Remove `Setup__Token`** and restart. The endpoint refuses once any admin exists.

## 7. Webhooks to register
| Service | URL |
|---|---|
| Paystack | `https://chewypetsfeeds.com/api/paystack/webhook` |
| AlatPay | `https://chewypetsfeeds.com/api/alatpay/webhook/chewypets` |
| Meta WhatsApp | `https://chewypetsfeeds.com/api/whatsapp/webhook` (verify token = `WhatsApp__VerifyToken`) |

## 8. What runs by itself
| When | What | On failure |
|---|---|---|
| Always | WhatsApp inbox worker: answers stored messages, 4 customers at a time, in order per customer; retries 15 s / 1 min / 5 min, then apologises to the customer and alerts you | alert |
| Every 5 min | Payment reconciler: settles paid links whose webhook was missed | logged |
| Every 5 min | Health check: services, API, public site, TLS, disk, RAM, backup + restore-test age, WhatsApp backlog, MySQL exposure | email + WhatsApp, hourly while it lasts, then "Resolved" |
| 02:30 UTC daily | Encrypted backup → R2 (14 daily, 8 weekly, 12 monthly), size-verified after upload | alert |
| Sun 03:30 UTC | Restore test: downloads the newest backup from R2, restores to a scratch DB, compares | alert |
| Daily | Security updates (unattended-upgrades) | — |

## 9. Operations
| Task | Command |
|---|---|
| Logs | `journalctl -u inventory-api -f` |
| Status | `systemctl status inventory-api nginx mysql` · `systemctl list-timers 'inventory*'` |
| Backup now / restore test now | `systemctl start inventory-backup` · `systemctl start inventory-restore-test` |
| Test alerts | `inventory-alert "Test" "Alerts work"` |
| Inbox | `mysql -e "SELECT Status, COUNT(*) FROM inventory_chewypets.inbound_messages GROUP BY Status"` |
| Banned IPs | `fail2ban-client status sshd` |

**Disaster recovery** (new server): provision (§2–3), then for each schema download the newest `daily/<schema>/*.age` from R2 and
`age -d -i owner.key file | gunzip | mysql`, then deploy (§4). Binary logs on the old disk (if it survived) replay changes after the dump.

## 10. Acceptance checklist
- [ ] `https://chewypetsfeeds.com` and `/inventory/` load; HTTP redirects to HTTPS; certificate valid
- [ ] `nmap -p 3306 <ip>` from outside: filtered/closed; SSH with a password is refused
- [ ] Admin and Clerk sign in; Clerk gets 403 on admin actions
- [ ] `reboot` → everything comes back with no manual steps
- [ ] `inventory-backup` put objects in R2; `inventory-restore-test` passes
- [ ] `inventory-alert "Test" "…"` arrives by email and WhatsApp; stopping `inventory-api` triggers an alert within 5 min
- [ ] A WhatsApp message to the business number is answered; `inbound_messages` shows it Done
