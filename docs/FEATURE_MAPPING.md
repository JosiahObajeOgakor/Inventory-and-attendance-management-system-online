# FEATURE_MAPPING — VB.NET screens → Angular features + API

Companion to `DISCOVERY.md` (rule IDs S*, P*, T*, C*, Q*, R*, A*, X*, defects D*).
Endpoints are **derived from the existing screens**, not from the brief's examples. Where the brief's
example has no counterpart in the desktop app, it is listed under "Not in existing app".

Release grouping is a *proposal* pending decision D3 in DISCOVERY.

## 1. Proposed target architecture

```
Browser ──HTTPS──▶ NGINX :443 ─┬─▶ /            Angular static files (dist/)
                               └─▶ /api/*  ──▶  Kestrel 127.0.0.1:5000  (systemd: inventory-api)
                                                    │
                                                    └─▶ MySQL 8 127.0.0.1:3306 (no public bind)
```

```
Inventory.sln
src/
  Inventory.Domain/          entities, enums, domain rules (money math, allocation, payment waterfall), interfaces — no EF/ASP.NET refs
  Inventory.Application/     use-cases (commands/queries), DTOs, FluentValidation validators, abstractions (IClock, ICurrentUser, IDocumentNumbers…)
  Inventory.Infrastructure/  EF Core + MySQL provider, migrations, Identity persistence, legacy-hash verifier, PDF/Excel, backups, migration importer
  Inventory.Api/             controllers, auth/authorization policies, middleware (exception → ProblemDetails, request logging), rate limiting, health
tools/
  Inventory.MigrationTool/   SQL Server → MySQL importer + verifier + report (console; never writes to SQL Server)
client/inventory-ui/         Angular (standalone components, lazy routes, reactive forms)
tests/
  Inventory.UnitTests/       domain rules ported from the 337 MSTest cases
  Inventory.IntegrationTests/ real MySQL (Testcontainers) — concurrency, rollback, authz
deploy/                      nginx conf, systemd unit, backup scripts, monitoring, runbook
docs/
```

Key design decisions (all reversible, none blocking Phase 2 except D1):
* **Framework:** .NET SDK 10 is installed → ASP.NET Core 10 (LTS). Angular via `npx @angular/cli` (Node 22 present; `ng` not installed globally).
* **MySQL provider:** verified in Phase 2 — Pomelo has no EF Core 10 release, so Oracle's `MySql.EntityFrameworkCore` 10.0.9 is used (see §5).
* **Auth:** ASP.NET Core Identity (users/roles) with a custom `IPasswordHasher` that verifies legacy `pbkdf2$120000$…` hashes and re-hashes to Identity's format on first successful login (users keep their passwords). Sessions: **HttpOnly, Secure, SameSite=Strict cookie** (simplest and safest for a same-origin SPA behind NGINX; avoids tokens in `localStorage`) + antiforgery header for mutating calls. Lockout 5/15 min replicated via Identity lockout. Rate-limit `/api/auth/*`.
* **Authorization:** policies `RequireAdmin`, `RequireStaff` (Admin or Clerk) applied per endpoint; a test enumerates every controller action and asserts it carries an explicit policy (fails the build on an unannotated endpoint).
* **Tenancy:** see D1. Encapsulated behind `ICompanyContext`; either choice is a data-layer detail, not an API change.
* **Money & rounding:** `decimal` everywhere; a single `Money` helper using `MidpointRounding.ToEven` (matches VB `Math.Round`) except price-book % adjustment (`AwayFromZero`, S1).
* **Stock engine:** one domain service, `StockService`, is the only writer of `stock_batches`. Every path (sale, purchase receipt, production, transfer, adjustment, invoice void, opening balance) writes a `stock_movements` row in the same transaction (closes D2, D4). FEFO allocation ported exactly (S5).
* **Concurrency:** pessimistic `SELECT … FOR UPDATE` in ascending product-id order + deadlock retry (error 1213) for stock/balances; unique constraints + retry for document numbers; optimistic tokens for ordinary edits; idempotency key on `POST /sales` and `POST /purchases` so a double-click/retry cannot double-post.
* **Document numbers:** keep the human format `<Prefix>-<ddMMyyyy>-<HHmmss>` (existing printed documents, barcodes on receipts) but generate them from a `document_sequences` counter with a collision-free suffix only on clash (`…-143205-2`), never `Thread.Sleep`. **[CONFIRM]** the client accepts the suffix.
* **PDF:** server-side (QuestPDF — community licence conditions must be checked, or an alternative; **verify licence before adopting**), replacing `DocPrinter`; Excel via ClosedXML; browser print CSS for quick print.
* **Logging:** Serilog structured logs → journald/file; never log passwords/secrets/PII bodies.
* **Not adopted (per brief):** Redis, queues, microservices, offline sync, Kubernetes.

## 2. Screen → Angular feature → API

`R1` = release 1 (go-live minimum), `R2`, `R3` = later. Roles: **A** Admin, **C** Clerk.

| Legacy screen | Angular route / feature | Endpoints | Roles | Rules | Rel |
|---|---|---|---|---|---|
| `frmLogin`, `frmSetPassword`, `frmRegister(firstAdmin)` | `/login`, `/change-password` | `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`, `POST /api/auth/change-password`, `POST /api/setup/first-admin` (only while no admin exists) | public / any | A1–A4 | R1 |
| Company picker | header company switcher; `GET /api/auth/me` returns allowed companies | `POST /api/auth/company` | any | §2 DISCOVERY | R1 |
| `ucDashboard` | `/dashboard` | `GET /api/dashboard` (Clerk payload omits profit/advice server-side) | A, C | T5 | R1 |
| `ucInventory` stock/batch views | `/inventory` | `GET /api/inventory/stock` (by product), `GET /api/inventory/batches`, `GET /api/inventory/movements?type=` | A, C | T5 | R1 |
| `frmAddItem` | `/inventory/products/new` | `POST /api/products` (with optional opening stock → writes `OPENING_BALANCE` movement) | A, C | D4 | R1 |
| `frmProductPrices`, `frmEditPrice` | `/products/:id` | `GET/PUT /api/products/{id}`, `POST /api/products/{id}/deactivate` | A | S11 | R1 |
| Delete product (`ucInventory`) | product detail | `DELETE /api/products/{id}` → **deactivate if referenced; hard delete only if unreferenced; never delete stock silently** | A | D3 | R1 |
| Categories / Warehouses (no screen today; combo-box data + seeded) | `/settings/categories`, `/settings/warehouses` | `GET/POST/PUT /api/categories`, `/api/warehouses` | A (write), all (read) | – | R1 |
| `frmProduction`, `frmEditProduction` (Chewy) | `/inventory/production` | `POST /api/stock/production`, `PUT /api/stock/production/{movementId}`, `DELETE …` (**reversal movement, not row delete**) | C add; A edit/delete | T1, T2, D6 | R1 |
| `frmMoveStock` (Chewy) | `/inventory/transfer` | `POST /api/stock/transfers` | A, C | T3 | R1 |
| *(not in existing app)* stock adjustment / count | `/inventory/adjust` | `POST /api/stock/adjustments` (`ADJUST`, reason mandatory) | A | T6 | **R2 — new** |
| `frmSerials` | `/inventory/serials` | `POST /api/serials/receive`, `GET /api/serials?productId=`, `GET /api/serials/{sn}/history`, `POST /api/serials/{sn}/write-off`, `POST …/take-back`, `GET /api/serials/discrepancies` | A | T7, S13 | R3 |
| Barcode labels / scan (`Barcodes.vb`) | product page "print label"; POS scan field | `GET /api/products/{id}/label.png`, `GET /api/products/by-barcode/{code}` | A, C | – | R1 (scan) / R2 (label) |
| `ucSuppliers` (Suppliers) | `/suppliers` | `GET/POST/PUT/DELETE /api/suppliers` | A | delete blocked if referenced | R1 |
| `ucSuppliers` (Purchases), `frmNewPO`, `frmPreviousSupplierDebt` | `/purchases`, `/purchases/new` | `GET /api/purchases`, `GET /api/purchases/{id}`, `POST /api/purchases` (idempotent; `receiveNow`, `paidNow`), `POST /api/purchases/{id}/receive` (**transactional, writes movements — replaces D2**), `POST /api/purchases/{id}/pay`, `GET /api/suppliers/{id}/balance` | A | P1–P6 | R1 |
| `ucCustomers`, `frmAddCustomer`, `frmCustomerMetrics` | `/customers` | `GET/POST/PUT/DELETE /api/customers`, `GET /api/customers/{id}/metrics` | A (full); C may `POST` (inline create) [CONFIRM] | C1, C3 | R1 |
| `frmRecordPayment` | customer detail | `POST /api/customers/{id}/payments` | A | C2 | R1 |
| `ucInvoices`, `frmNewInvoice`, `frmPreviousDebt` | `/sales`, `/sales/new` | `GET /api/sales?…`, `GET /api/sales/{id}`, `POST /api/sales` (idempotent), `POST /api/sales/preview` (totals + shortfalls, no write), `GET /api/customers/{id}/balance` | A, C (profit fields A only, server-side) | S1–S13 | R1 |
| Delete invoice | sale detail | `POST /api/sales/{id}/void` (**reverses stock via movements, customer balance, ledger, serials, rebate; keeps the invoice row with `Voided` status**) — replaces destructive delete (D1) | A | D1 | R1 [CONFIRM D5] |
| `frmInvoiceReceipt` | `/sales/:id/receipt` | `GET /api/sales/{id}/receipt.pdf`, `GET /api/sales/by-number/{n}` (scan) | A, C | X4 | R1 |
| `ucQuotations`, `frmNewQuotation` | `/quotations` | `GET/POST /api/quotations`, `GET /api/quotations/{id}`, `DELETE` (A), `POST /api/quotations/{id}/convert` (**one transaction — fixes D10**), `GET …/pdf` | A, C (delete A) | Q1 | R2 |
| `ucWaybill`, `frmNewWaybill` | `/waybills` | `GET /api/waybills?pending=`, `POST /api/waybills`, `GET /api/waybills/{id}.pdf` | A, C | – | R2 |
| `ucPriceList`, `frmPriceUpdate`, `frmAddPriceListProducts`, `frmSendPriceList` (Candid) | `/price-list` | `GET /api/price-list`, `GET /api/price-list/history`, `POST /api/price-list/update`, `POST /api/price-list/products`, `GET /api/price-list/export` | A edit; C view/send | PriceBook | R3 (company flag `hasPriceLists`) |
| `ucFinance` | `/finance` | `GET /api/finance/summary`, `GET /api/ledger`, `DELETE /api/ledger/{id}` [CONFIRM: destructive] | A | X2, D7 | R1 |
| `ucExpenses` | `/finance/expenses` | `GET/POST/DELETE /api/expenses` | A | – | R2 |
| `ucIncome` + `Exporter.RunPeriodReport` | `/finance/income` | `GET /api/reports/income?year=&month=`, `GET /api/reports/period.xlsx|.pdf` | A | X1 | R1 |
| `ucRebates` | `/finance/rebates` | `GET /api/rebates`, `POST /api/rebates/redeem`, `PUT /api/settings/rebate-rate` | A | S9 | R2 |
| `ucEmployees`, `frmAddEmployee`, `frmPayrollHistory` | `/employees` | `GET/POST/PUT /api/employees`, `POST /api/payroll/generate`, `PUT /api/payroll/{id}`, `POST /api/payroll/{id}/pay`, `POST /api/payroll/pay-all`, `GET/POST /api/loans`, `POST /api/loans/{id}/repayments` | A | R1–R3 | R3 |
| `Attendance` + `frmClerkWelcome` | on clerk login; `/attendance` (A report) | `POST /api/attendance/check-in`, `POST /api/attendance/decline`, `POST /api/attendance/check-out`, `GET /api/attendance?from&to` | C, A read | Attendance | R3 [CONFIRM] |
| `ucStaff`, `frmRegister`, account menu | `/admin/users` | `GET/POST /api/users`, `PUT /api/users/{id}`, `POST …/reset-password`, `POST …/disable` | A | D14 | R1 |
| Backup / Export all / Archive | `/admin/system` | export: `GET /api/admin/export.xlsx`; backup: **server cron, not an API**; archive [CONFIRM] | A | – | R2 |
| `LoginAudit` view | `/admin/audit` | `GET /api/audit/logins`, `GET /api/audit` | A | brief §26 | R2 |
| `ucAIAssistant` / Ollama | — | — | — | D6 | **dropped (proposed)** |
| Forecasting / market basket / anomalies / `SampleData` | dashboard "attention" panel (A) | `GET /api/insights/reorder` | A | T4 | R1 = simple reorder advice (T4); rest R3 [CONFIRM] |
| Licensing, Appearance/theme, Igbo `Lang`, idle warning | — | — | — | – | dropped / R3 (Igbo). Idle: **server session sliding expiry** (Admin 5 min idle equivalent [CONFIRM]) |
| *Health* | – | `GET /api/health` → `{"status":"healthy"}` (DB ping, no detail) | anon | brief §24 | R1 |

### Brief's example endpoints with no counterpart today
`/api/categories`, `/api/warehouses` (CRUD screens don't exist; only pickers), `/api/inventory/adjust` & `issue` (no adjustment/"issue" concept — issuing is a *sale*), `/api/reports/stock` and `/reports/sales` as such (existing reports are the income/period report, low-stock view and valuation on the dashboard). These will be built only if you confirm.

## 3. Incompatibilities: VB.NET / SQL Server / WinForms → C# / ASP.NET Core / Angular / MySQL

| # | Area | Old | New | Handling |
|---|---|---|---|---|
| 1 | Tenancy | DB-per-company, accounts mirrored across DBs, UserIDs differ | D1 | re-key users by `Username` during import |
| 2 | Auth | in-process PBKDF2, UI-only role checks | Identity + policies | legacy-hash verifier; re-hash on login |
| 3 | Session model | desktop process, admin idle timer, 10-min unlicensed cap | cookie session | sliding expiry; licence cap dropped |
| 4 | Rounding | VB `Math.Round` banker's (ToEven); `CDec`/`CInt` truncation semantics | C# `Math.Round(decimal, 2)` default is also ToEven | explicit `MidpointRounding` everywhere + golden tests from MSTest vectors |
| 5 | Integer/decimal | `Integer` (Int32); `/` on Decimals; `\` operator | C# `int`; `/` between ints is integer division | audit every VB `/` and `\` on port |
| 6 | Date/time | `Date.Today`, `DateTime.Now` = client PC local; SQL `SYSDATETIME` | server UTC; browser TZ | one `IClock`; Lagos business-day helper; "today" = Lagos date |
| 7 | Strings | No `Option Compare` is set anywhere → VB `=` on strings is **binary (case-sensitive)**, while the same values compared in SQL Server (`Username = @u`, `Status = 'Paid'`) are case-*insensitive*. Code mixes both (e.g. `CurrentUserRole = "Admin"` is case-sensitive; `String.Equals(..., OrdinalIgnoreCase)` used for `Walk-in`). Also VB `&`, 1-based `Mid`, `vbCrLf` | C# ordinal by default; MySQL `_ci` collation | port each comparison deliberately; usernames unique case-insensitively (as today via SQL) |
| 8 | Nullability | `DBNull`, `IsDBNull`, `If(x, DBNull.Value)` | `null`, nullable reference types | DTOs with explicit nullables |
| 9 | Data access | `DataTable`/ad-hoc SQL string concat (`$"SELECT {col}…"` for tier column, `SELECT * FROM {table}` in export/archive) | EF Core + LINQ | column/table names never from input; whitelist mappings (tier→column) |
| 10 | Locking | `UPDLOCK, HOLDLOCK` | InnoDB `FOR UPDATE`, gap locks, deadlock 1213 | see DATABASE_MAPPING §5 |
| 11 | Identity/ids | `SCOPE_IDENTITY` | EF keys | – |
| 12 | Error codes | 2627/2601/547 | 1062/1451/1452 | typed exceptions in Infrastructure |
| 13 | Text | `NVARCHAR`, CI+AS collation | `utf8mb4` collation choice | `_as_ci` |
| 14 | Files | `%LOCALAPPDATA%\StockDesk`, `AppPaths.Asset`, `Image.FromFile` for stamps/logos | server static assets | store logos/stamps as company assets; `TrimMargins` pre-processed once |
| 15 | Printing | GDI+ `PrintDocument` | HTML/PDF | server PDF |
| 16 | Excel | ClosedXML .NET Fx | ClosedXML .NET 10 | version bump |
| 17 | Barcodes/QR | hand-rolled Code128 + QRCoder | QRCoder (verify net10 support) + JsBarcode or port | `Barcodes.ReadScan` accepts plain number **and** `CS|I|…` QR payload — keep |
| 18 | Threading | `Thread.Sleep` retries; `Task.Run` daily backup | request-scoped, DB-driven | no sleeps; cron |
| 19 | Localisation | English + Igbo runtime catalogue | Angular i18n | R3 |
| 20 | Config | `App.config` (addresses, bank accounts, VAT, AI URL) | `appsettings` + `company_settings` + env secrets | no secrets in git |
| 21 | Concurrency model | single user per PC on LocalDB | multi-user | idempotency keys, locks, tokens |
| 22 | Reports | `DataTable` bound to grids, PagedGrid client-side paging | server-side paging/sorting | all list endpoints paginated |
| 23 | Licensing | machine-bound key, WMI | none | removed |
| 24 | Deployment | Inno Setup + bundled LocalDB | Oracle VM | `deploy/` |
| 25 | Testing | MSTest on `net48` with a real LocalDB | xUnit + Testcontainers MySQL | port test vectors, not code |

## 4. Test strategy (Phase 6 alignment)

Port the *cases* of the 337 MSTest tests as characterization tests before writing the new services (`SaleTests`-equivalents in `StockTests`, `CustomerDebtTests`, `SupplierDebtTests`, `PayrollTests`, `QuotationTests`, `ArchiveTests`, `CompanyTests`, `DataSafetyTests`…). Plus the brief's list: valid/invalid login, Admin vs Clerk on every endpoint, purchase, sale, adjustment, insufficient stock, **N parallel sales of the last unit (exactly one succeeds)**, forced mid-transaction failure rolls everything back, and SQL Server-vs-MySQL control-total comparison.

## 5. Phase 2 findings (verified, 2026-09-19)

* **MySQL provider:** Pomelo's newest stable is 9.0.0 (EF Core 9) — no EF Core 10 release. Chosen: **Oracle `MySql.EntityFrameworkCore` 10.0.9** on .NET 10 / EF Core 10 (licence GPL-2.0 with the Universal FOSS exception; fine for a privately hosted app that is not redistributed — revisit if the software is ever sold/distributed). Verified working against MySQL 8.4 in Docker.
* **Provider quirks found and handled:** `DateOnly` is not mapped natively (MySql.Data returns `DateTime` for `DATE`) → value converters in `BusinessDbContext`. `RowVersion`/rowversion concurrency tokens are not a MySQL concept → dropped in favour of pessimistic `SELECT … FOR UPDATE` on stock batches, customer, supplier and open documents.
* **Locking model:** READ COMMITTED transactions + explicit `FOR UPDATE` in ascending product-id order; whole unit retried on deadlock (1213/1205) or duplicate document number (1062).
* **Verified by integration tests on real MySQL** (`tests/Inventory.IntegrationTests`): sale writes invoice/lines/stock/movement/ledger/payment/rebate/audit atomically; insufficient stock writes nothing; mid-transaction failure rolls back; 10 parallel sales of 3 units → exactly 3 succeed, stock 0, balance consistent; idempotency-key replay sells once; overflow payment settles older invoices oldest-first.
