# MIGRATION_PLAN — SQL Server → MySQL data migration and cut-over

Principles (from the brief): the source is **never modified or deleted**; the process is repeatable and
idempotent; every run ends with a verification report. Column-level rules: `DATABASE_MAPPING.md`.

## 0. Preconditions (blocking)

1. **Identify the real production database** (DISCOVERY D2). The local LocalDB is dev-sized (12 invoices). Obtain a **`.bak`** of each business DB from the client's machine (the app already writes `.bak` files to `%LOCALAPPDATA%\StockDesk\Backups`). Restore the `.bak` **as a copy** (new name) on a dev SQL Server; migrate from the copy. The live client DB is never touched.
2. Decide D1 (tenancy), D3 (scope), D7 (time zone), D5 (defect handling).
3. Freeze date agreed with the client (see cut-over).

## 1. Tool

`tools/Inventory.MigrationTool` (C# console, `Microsoft.Data.SqlClient` read-only login → EF Core/MySQL writer).
* Source connection uses a **read-only SQL login** (`db_datareader` only) — the tool *cannot* write to SQL Server even by mistake.
* Modes: `plan` (dry-run, counts + anomalies only), `import`, `verify`, `report`. `import` runs in a single MySQL transaction per table group and is re-runnable: it targets an **empty schema** (`--rebuild` drops/recreates the target schema only) *or* upserts by original primary key (idempotent), never appends duplicates.
* Secrets via environment variables; nothing committed.

## 2. Steps

| # | Step | Detail |
|---|---|---|
| 1 | **Extract** | Read each table in FK order (DATABASE_MAPPING §6). Stream; no full-table memory load. Record source row count + checksum (hash of ordered PK list, and Σ of every `DECIMAL` column). |
| 2 | **Pre-flight validation** (source) | Report, don't fix silently: orphan FKs (`Waybills.CreatedByUserID`, `Ledger` refs), duplicate barcodes after `''→NULL`, negative stock, `Customers.Balance` ≠ Σ unpaid invoices, `Suppliers.Balance` ≠ Σ unpaid POs, `Invoices.TotalAmount ≠ Subtotal − Discount + VAT`, `Σ InvoiceItems.LineTotal ≠ Subtotal`, `Status` inconsistent with `AmountPaid`, `IsSample = 1` rows, placeholder password hashes, non-ASCII/over-length strings, `datetime2` fractional digits > 6, `Ledger.Reference` at max length. |
| 3 | **Transform** | Type conversions per DATABASE_MAPPING (§1–3). `''` barcode → NULL. `datetime2`→`DATETIME(6)` with the agreed zone shift. `WorkDate` recomputed. `Attendance` (legacy) skipped. `AppSettings` filtered to business keys. **Users**: keyed by `Username`; both DBs' users merged into one identity store; old→new UserID map persisted (`migration_id_map`) and used to rewrite `CreatedByUserID/UserID/ReceivedByUserID/ChangedByUserID/EmployeeMonthly…` in each company's data. Roles: `Admin→ADMIN`, `Warehouse Clerk→CLERK`. |
| 4 | **Load** | Explicit IDs preserved (per company under D1-(a); under D1-(b) each company's IDs are re-keyed with an offset/map and every FK rewritten through the map — *this is the risky option; hence a decision*). Reset `AUTO_INCREMENT` after load. |
| 5 | **Synthesize history** | For stock that pre-dates movement logging (`frmAddItem` opening qty, `PO-RECEIPT` receipts, production edits), create `OPENING_BALANCE` movements so that **Σ(IN) − Σ(OUT) per (product, warehouse) = Σ QuantityOnHand**; flagged `Reference='migration'`. Reported per product. Nothing is invented that isn't already implied by the current batch quantities. |
| 6 | **Verify** | (a) row count per table equals source (except documented exclusions); (b) FK integrity via `information_schema` + anti-join queries; (c) money control totals equal to the cent: Σ`Invoices.TotalAmount/AmountPaid/VATAmount`, Σ`InvoiceItems.LineTotal`, Σ`Payments.Amount`, Σ`PurchaseOrders.TotalAmount/AmountPaid`, Σ`Customers.Balance`, Σ`Suppliers.Balance`, Σ`Ledger` by type/entry, Σ`Expenses`, Σ`RebateEntries` by status, Σ payroll; (d) stock: Σ`QuantityOnHand` per product/warehouse; (e) per-business-day invoice counts; (f) sampled row-level diffs (e.g. 1 000 random invoices with items compared field-by-field); (g) **login test**: for every migrated user with a real hash, verify the legacy PBKDF2 hash *string* survived byte-for-byte (we can't test passwords without them; user logs in once to prove). |
| 7 | **Report** | `migration-report-<company>-<timestamp>.md`: counts, totals side-by-side, exceptions with row ids, transformations applied, time zone assumption, tool version/git SHA, duration. Exit code non-zero on any verification failure. |

## 3. Data that is *not* migrated (and why)

`AppSettings`: `theme.*`, `app.lang`, `backup.lastDate`, `license.*` (desktop/licensing). Legacy `Attendance` table (superseded). `LoginAudit.MachineName` kept as history only. `Assets/landingvideo.mp4` (desktop UI) unless requested. Sample rows (`IsSample=1`): **migrated flagged**, and the report lists them; a `--exclude-sample` switch removes them if the client confirms they're demo data.

## 4. Rehearsals and cut-over

1. **Rehearsal 1–n** (on a copy): run `plan` → `import` → `verify` until the report is clean; client reviews sample screens against their known figures (yesterday's sales, a customer's balance, stock of top products).
2. **UAT** on the Oracle VM with the migrated copy; two real users walk through the R1 workflows.
3. **Freeze:** agreed time; client stops using the desktop app (they should take a final Export-all + `.bak`).
4. **Final run:** fresh `.bak` → restore copy → `import` → `verify` → report signed off. Time-box; the desktop app remains the system of record until sign-off.
5. **Go-live:** DNS/HTTPS live; desktop app set to read-only in practice (instruct users; the desktop app can't be remotely disabled — **the licensing server could be used to lock it if you want; your call**).
6. **Fallback:** original SQL Server DB + `.bak` retained untouched ("formally accepted" = written client sign-off, then keep ≥ 90 days). Any post-cut-over transactions in MySQL are *not* back-portable — the rollback window is therefore the first day; after that, forward-fix only. Stated plainly so it is an informed decision.

## 5. Idempotency and safety rules

* Source login is read-only.
* Target: `import` refuses to run against a non-empty schema unless `--upsert` (PK-keyed `INSERT … ON DUPLICATE KEY UPDATE`) or `--rebuild` is given; `--rebuild` only works when the connection string's database name ends `_staging` or `--i-am-sure <dbname>` matches exactly.
* Every run writes a run-id; rows imported carry no marker columns (schema stays clean) — the report and the id map table are the audit.
* No step deletes or updates anything in SQL Server.

## 6. Deployment/ops plan (Phase 7 outline — details when we get there)

| Item | Plan |
|---|---|
| VM | Oracle Cloud Always Free `VM.Standard.A1.Flex` 2 OCPU / 12 GB, Ubuntu 22.04/24.04 **arm64** — publish the API `linux-arm64`; MySQL 8 arm64 package; NGINX. **Capacity for A1 in the chosen home region is sometimes unavailable**; confirm region/quota first. Oracle can reclaim idle Always Free instances — keep it actively used and monitored. |
| Network | OCI security list/NSG: ingress 22 (restrict to your IP), 80, 443 only. `ufw` mirrors it. MySQL `bind-address=127.0.0.1`; Kestrel on `127.0.0.1:5000`. |
| TLS | Let's Encrypt via `certbot --nginx`; HTTP→HTTPS redirect; HSTS after verification; renewal timer + expiry monitor. Needs the domain's A record → VM public IP first. |
| Service | `inventory-api.service`: `User=inventory` (no login shell), `Restart=always`, `EnvironmentFile=/etc/inventory/api.env` (mode 600, root-owned; holds `ConnectionStrings__Default`, auth keys). Logs to journald. |
| Config/secrets | env file on VM only; repo has `.env.example` with placeholders. |
| Backups | Daily `mysqldump --single-transaction --routines` (per company DB) → gzip → **encrypted** (`age`/`gpg`) → **Oracle Object Storage bucket (Always Free 20 GB)** via OCI CLI/rclone; retention 7 daily + 4 weekly (+ optionally monthly); a **weekly automated restore test** to a scratch DB; failure → alert. A local copy on the VM is allowed as a convenience but never the only one. |
| Monitoring | Health endpoint checked externally (free UptimeRobot/Healthchecks.io, or OCI Monitoring); cron script for disk %, RAM, service states (`mysql`, `inventory-api`, `nginx`), cert days-to-expiry, "last successful backup age"; alerts by email. |
| Deploy from repo | `deploy/` scripts + runbook: build (API `dotnet publish -r linux-arm64`, Angular `ng build`), rsync, migrate schema (`dotnet ef migrations bundle`), restart service — reproducible from a clean VM. |

## 7. Risks

| Risk | Mitigation |
|---|---|
| Real data isn't what's on this machine | Precondition 0.1; nothing is declared "migrated" until run on the real `.bak` |
| Multi-company re-keying errors | Prefer per-company schema (D1-a) or heavy verification + id map |
| Rounding/regression in money maths | golden vectors from MSTest; totals reconciliation to the cent |
| Time-zone assumption wrong | report shows sample before/after; decision D7 up front |
| Lock/deadlock behaviour differs (InnoDB) | ordered locking + retry + concurrency integration tests |
| MySQL EF provider vs EF Core 10 support | verify at start of Phase 2; fallback options in FEATURE_MAPPING |
| Scope (payroll, analytics, AI, Igbo, video…) inflates effort | release grouping R1/R2/R3, D3 |
| Users lose passwords | legacy hash verifier keeps existing passwords; placeholder-hash users get "set password" flow |
| Oracle A1 capacity / idle reclamation | check region before committing; keep the VM active; backups off-VM |
