# STATUS — web rewrite (updated 2026-09-19)

## Done and verified (93 backend tests + a 20-step browser walkthrough, all passing on real MySQL 8)
| Area | State |
|---|---|
| Discovery docs | `DISCOVERY.md`, `DATABASE_MAPPING.md`, `FEATURE_MAPPING.md`, `MIGRATION_PLAN.md` |
| Domain rules | money rounding (banker's), sale/purchase totals, payment waterfall, FEFO allocation, restock advice, rebate, ranking, barcodes — 24 unit tests |
| Core workflows (Application) | sales, purchases (+receive, pay), production (+correct), transfers, adjustments (new), products, customer payments, invoice **void** (replaces destructive delete), suppliers/customers CRUD |
| Defects fixed in the port | D1 void restores stock/balance, D2 transactional+logged PO receive, D3 no silent stock deletion, D4 opening balance logged, D6 production "delete" keeps history, D8 ledger ref widened, D10 (server side ready) |
| Concurrency/safety | row locks in fixed order, deadlock/duplicate retry, idempotency keys, CHECK(qty>=0), 10 parallel sales of 3 units → exactly 3 succeed |
| API | 11 controllers, explicit policy on every action (test enforces it), Admin/Clerk matrix tested, cost/profit hidden from clerks server-side, RFC7807 errors without internals, health endpoint |
| Auth | Identity + cookie (HttpOnly/Secure/Strict), legacy PBKDF2 hashes verified then re-hashed, 5-try/15-min lockout, rate-limited login, generic failure message, forced password change, first-admin only with one-time token |
| Data layer | EF Core migrations for company schema + identity, one MySQL schema per company |
| Migration tool | reads SQL Server (SELECT-only), imports both companies with original IDs, users merged by username, synthesizes opening balances, refuses non-empty target, control totals to the cent, integrity + consistency checks, report. **Run on the local dev DBs: both PASS** |
| Deployment files | NGINX, systemd (hardened), MySQL loopback config, ufw/iptables, encrypted off-VM backups with 7 daily + 4 weekly retention, restore test, health/disk/RAM/TLS/backup monitor, deploy script, runbook — shellcheck-clean, **not yet run on a real VM** |

## Angular UI (added)
Angular 21 (zoneless, standalone, signals, reactive forms, lazy routes; Angular 22 needs a newer Node than this PC has). Design: stencil display type for headings and status stamps, mono figures for money and identifiers, per-business tint (green ChewyPets, plum Candid) so nobody sells in the wrong company. Screens: sign-in, forced password change, dashboard, sales (list, POS, receipt with print, void), stock (batches, history, production, transfer, adjust), products, customers (+record payment), suppliers, purchases (list, new, receive, pay), finance, people & access, activity log. Admin idle warning (5 min + 3 min countdown), responsive down to tablet. **Verified in a real browser (Edge via Playwright) against the real API and the migrated dev data**: 20-step walkthrough incl. a complete sale, void, business switch, clerk restrictions. Not yet done: automated UI tests in the repo (the walkthrough script is not committed), quotations/waybills/rebates/expenses/payroll/attendance screens, PDF/Excel export.

## Added since (2026-09-20)
Everything from the desktop app now exists on the web except the data migration of the new tables (see below).
- **Documents (PDF, QuestPDF):** receipt with diagonal watermark and Code 128 barcode, quotation, waybill (dispatch stamp), price list, shelf label. Company profile (address, phone, email, TIN, up to 3 banks, logo/signature/stamp uploads) at *Company & documents*.
- **Trade:** quotations (convert to sale in ONE transaction; the desktop app did it in two), waybills.
- **Staff:** attendance (clerk check-in prompt, decline, sign-out logs check-out, admin per-day and every-event report), employees, monthly payroll, loans (a sixth of the balance deducted, oldest loan first; paid rows frozen; net pay posts a Salaries expense).
- **Stock:** serial numbers (receive, sell with exact serials, void returns them, take back, write off, stock-check discrepancies).
- **Money:** expenses, rebates (redeem, default rate), price book for Candid (edit grid, percent move with rounding, add products, full history), customer metrics, Excel exports (inventory, invoices, customers, expenses, ledger, period report, full pack).
- **Dashboard (admin):** KPI cards vs last month, top-customer dot matrix with a month timeline that plays, revenue/income/profit chart (30 days / 12 months, refreshes every 30 s while visible), warehouse stock bubbles, calendar of who owes what and when (tooltip on hover/focus), largest overdue, products not selling.
- **Assistant (OpenAI, admin only):** answers only through nine read-only functions over the signed-in company's records; 10 questions per person per rolling 24 h (kept in the identity DB, refunded if the call fails); default model gpt-4.1-nano (set OpenAI__Model); dashboard recommendations cached 12 h with a rule-based fallback; suggests SpringuptechAfrica Limited for marketing underperforming products.
- **Email (Zoho SMTP via MailKit):** quotation and price-list emails with the PDF attached, branded per company, preview shown before sending, audited, 30 per user per hour.
- **Secrets:** `.dev/secrets.env` locally, `/etc/inventory/api.env` on the VM (see `src/Inventory.Api/.env.example`).

## Added on 2026-09-20 (second pass)
- **Analytics** (ported from the VB): demand forecast with a measured error, products sold together, unusual movements, advice at the till and on purchase orders, customer segment.
- **Database storage & archive**, **Igbo language** (with override file), **Appearance settings**, **customer History dialog**, **shelf-label button**, **Excel buttons** on every list.
- **Paystack payment links** on quotations and on invoices with a balance (clickable in the PDF and the email, exact amount). A signed webhook is confirmed with Paystack, recorded once against the invoice, and the admin is told by email (and by SMS once a Termii key is configured).
- **Clerk welcome screen** with a Clock in button (clip slot: `client/inventory-ui/public/clerk-welcome.mp4`).
- Tests: 53 unit + 125 integration.

## Not built yet
1. **Migration of the new tables** (quotations, waybills, attendance, employees, payroll, loans, serials, price changes, company profile) from SQL Server. The tool still imports core tables only.
2. Automated UI tests in the repo (the walkthrough script is not committed).
3. A real-VM rehearsal of `deploy/`.
4. Removing the VB sources: hold until the migration above has run on the production backup and the two systems agree.

## Needs your input
Real company address/phone/email/TIN for both businesses; Zoho mailbox + app password; OpenAI key in the server env (rotate the one pasted in chat); production `.bak`; timezone confirmation; D5/D7/D11/D12 money-rule decisions; domain name; backup bucket; whether Candid clerks may record purchases.
