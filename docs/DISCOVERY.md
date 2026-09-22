# DISCOVERY — StockDesk / ChewyStock (VB.NET WinForms → web)

Phase 1 output. Everything here was read from the source in this repository and from
read-only metadata queries against the local SQL Server LocalDB (`StockDeskDB`,
`CandidPurrfectDB`). Nothing was modified. Items marked **[CONFIRM]** are assumptions
or ambiguities that need an owner decision; nothing has been invented to fill them.

---

## 0. Headline findings (read this first)

The brief describes ~10 screens / ~11 tables / 1 database / 2 users. The real system is materially larger:

| Brief says | Reality |
|---|---|
| ~10 screens | **~17 navigable screens + ~30 dialogs** (`frmMain.vb` nav; 50 `.vb` UI files) |
| ~11 tables | **30 tables** (+1 legacy `Attendance`) and 6 views in `StockDeskDB`; 31 in `CandidPurrfectDB` (adds `PriceChanges`) |
| One database | **Two businesses, two databases** (ChewyPets Feed → `StockDeskDB`; Candid Purrfect → `CandidPurrfectDB`) with a *shared* account table in the home DB that is mirrored into the other at sign-in (`Company.vb`, `Auth.vb`) |
| Products/categories/suppliers/purchases/sales/inventory | Also: quotations, waybills, customer credit + rebates, supplier payables, ledger, expenses, payroll + staff loans, attendance, serial-number tracking, barcodes/QR, price book (Candid only), demand forecasting / market-basket / anomaly analytics, local-LLM assistant, Igbo/English UI, licensing ($250 activation), themes |
| "Existing production data" | Local DBs hold **dev-scale data** (12 invoices / 6 products / 3 users in Chewy; 7 / 2 / 2 in Candid). Backups in `%LOCALAPPDATA%\StockDesk\Backups` are ≤9 MB. **[CONFIRM] Where is the real production database?** It is probably on the client's laptop, not this machine. The migration tooling must be run against *that*. |
| Roles Admin / Clerk | Roles are literally **`Admin`** and **`Warehouse Clerk`**. Enforcement exists **only in the desktop UI** (buttons/tabs hidden). There is no server-side enforcement today because there is no server. |

**Consequence:** this is not a 10-screen port. It is a ~19k-line application (`*.vb` excluding tests) with a 337-method MSTest suite. Scope must be agreed before Phase 2 — see §9 (decisions).

The MSTest suite (`tests/ChewyStock.Tests`, 337 `[TestMethod]`s) is the best asset for the rewrite: it is an executable statement of the business rules and should be mined for characterization tests.

---

## 1. Repository structure

| Path | What it is | Migrates? |
|---|---|---|
| `StockDesk.vbproj` | SDK-style, `net48`, WinForms, assembly `ChewyStock.exe`. Packages: System.Text.Json, ClosedXML (xlsx), QRCoder | Replaced |
| `Program.vb` | Entry: activation → first-admin → login loop → clerk welcome → `frmMain` | Replaced |
| `Schema.sql`, `Migration_v2.sql`, `Migration_v3.sql` | Base schema + upgrades. **Also** `DbBootstrap.Migrate()` applies further ALTERs in code | Source of truth for schema (verified against live DB — no drift except legacy `Attendance`) |
| `DataAccess.vb` | ADO.NET helper; all SQL parameterized; `InTransaction` helper | Replaced by EF Core |
| Domain modules: `Sales.vb`, `Purchasing.vb`, `Stock.vb`, `Serials.vb`, `Quotations.vb`, `PriceBook.vb`, `PriceLists.vb`, `PriceOverrides.vb`, `Payroll.vb`, `Staff.vb`, `Attendance.vb`, `Barcodes.vb`, `Numbering.vb`, `Auth.vb`, `Security.vb`, `Company.vb`, `CompanyData.vb` | Business logic — the real specification | **Port** |
| Reporting: `Exporter.vb`, `DocPrinter.vb` (52 KB), `frmInvoiceReceipt.vb`, `ucIncome.vb`, `ucFinance.vb` | Excel/CSV/PDF/print | Port (PDF via server) |
| Analytics: `Insights.vb`, `Forecasting.vb`, `Learning.vb`, `DemandModel.vb`, `MarketBasket.vb`, `PurchaseAdvice.vb`, `SaleAdvice.vb`, `SampleData.vb` | Deterministic forecasting/advice, demo-data generator | **[CONFIRM]** scope |
| `OllamaClient.vb`, `ucAIAssistant.vb` | Local LLM chat (Ollama on localhost) | **[CONFIRM]** — no local LLM on the Oracle VM (see §9) |
| `Licensing.vb`, `frmActivation.vb`, `licensing-api/` (Node) | Machine-bound licence, $250 lifetime, hosted on render.com | **Not applicable to a hosted web app** [CONFIRM drop] |
| Desktop-only: `Theme.vb`, `Anim.vb`, `CardPanel.vb`, `PagedGrid.vb`, `UiHelpers.vb`, `AppUI.vb`, `IdleWatcher.vb`, `CrashLog.vb`, `frmAppearance.vb`, `frmClerkWelcome.vb` (video), `installer/`, `Assets/` | WinForms chrome, installer | Not ported (assets reused: logos, stamps) |
| `Lang.vb` | English + Igbo UI catalogue | **[CONFIRM]** keep Igbo? |
| `DbMaintenance.vb`, `frmArchive.vb`, `BackupService.vb` | LocalDB size cap (10 GB), archive-and-delete of old paid records, daily `.bak` | Replaced by MySQL backups; **archive feature [CONFIRM]** |
| `.vscode/mcp.json`, `.claude/` | Tooling | Ignore |

**Git hygiene:** `bin/` and `obj/` are untracked (last commit "Stop tracking build output"). `licensing-api` is a **git submodule** (gitlink `160000`, currently showing local modifications `m`), so its `node_modules`/`local.db` are not part of this repo. It is out of scope for the port. `App.config` commits real bank account numbers and a licence-server URL; harmless for the new app but not to be copied into the new repo's config.

---

## 2. Multi-company architecture (the biggest structural finding)

* Two `Company` objects: **ChewyPets** (`database=Nothing` → whatever App.config names → `StockDeskDB`, is "Home") and **CandidPurrfect** (`CandidPurrfectDB`).
* The login screen picks the business; **all** data (products, stock, customers, invoices, attendance, numbering) is per-business. The code comment is explicit: a `CompanyID` column was rejected because one forgotten `WHERE` would leak data across businesses.
* **Accounts are shared**: `Users`/`Roles`/`LoginAudit` live in the home DB. On sign-in, `Auth.MirrorAccount` `MERGE`s the user into the other DB so that FKs (`CreatedByUserID`, `UserID`…) resolve there. `UserID`s therefore **differ between the two databases** for the same person.
* Per-company behaviour flags: `HasPriceLists` (Candid only: price book + "send price list"), Candid "buys rather than produces" (Inventory shows *Purchase history* and *Record purchase* instead of *Production history*, *Stock moves*, *Move stock*). Documents carry a per-company prefix, logo, signature/stamp, waybill stamp and bank accounts (from App.config).
* `CompanyData.ClearStarterData` deletes the Chewy seed rows (SKU-1001…, TIN-…) from a new Candid DB on first open.

**Implication for the target design** (needs a decision, §9-D1): the web app must either
(a) keep *database-per-company* (one MySQL schema each; per-request company resolved from the authenticated session; one shared identity store), or
(b) collapse to a single database with a `CompanyId` on every business table.
Option (a) preserves the existing isolation guarantee and maps 1:1 onto the two source DBs; option (b) is the conventional web approach but reintroduces the "forgotten WHERE" risk the original authors avoided (mitigated in EF Core by global query filters). **Recommendation: (b) with EF Core global query filters + a company-scoped `DbContext` + tests proving isolation** *or* (a) if you'd rather keep exact parity — I need your call. Either way IDs differ across the two source DBs, so migration cannot simply union the tables without re-keying.

---

## 3. Screen inventory

Navigation source: `frmMain.BuildTabs` / `ShowScreen`. Role gating source: `NavItem.AdminOnly` and `isAdmin` checks in each control.
Legend: **A** = Admin only, **A+C** = both.

| # | Screen (VB) | Nav | Role | Purpose | Key tables | Notes |
|---|---|---|---|---|---|---|
| 1 | `ucDashboard` | Dashboard | A+C | Stock value, low-stock count, today's invoice count, recent invoices, low-stock list. **Admin extras:** gross profit (latest month), month expenses, reorder/"needs attention" advice panel | `vw_StockValuation`, `vw_LowStock`, `Invoices`, `vw_ProfitAndLoss`, `Expenses`, analytics | Clerk never sees profit/advice |
| 2 | `ucInventory` | Inventory | A+C (partial) | Views: *Stock by product*, *Batches & expiry*, and per company: **Chewy** *Production history*, *Stock moves*; **Candid** *Purchase history*. Search, CSV/XLSX export, shelf label (Code128), add item, record production/purchase, move stock, send price list (Candid) | `Products`, `StockBatches`, `StockMovements`, `Categories`, `Warehouses`, `PurchaseOrders*` | **Clerk can:** add item, record production/purchase, move stock, print label, export. **Admin only:** edit prices, serials, edit/delete production entry, **delete product** |
| 3 | `frmAddItem` | dialog | A+C | New product + opening batch (SKU, name, category, unit, reorder level, cost, 3 prices, barcode, warehouse, batch no., expiry, qty) | `Products`, `StockBatches` | Opening qty is inserted **without a `StockMovements` row** (see §6 defects) |
| 4 | `frmProduction` / `frmEditProduction` | dialog (Chewy) | A+C add; A edit/delete | Record a production run into a warehouse; admin correction of an entry | `StockBatches`, `StockMovements` | `Stock.RecordProduction`, `Stock.CorrectProduction` |
| 5 | `frmMoveStock` | dialog (Chewy) | A+C | Warehouse→warehouse transfer | `StockBatches`, `StockMovements` | `Stock.TransferStock` |
| 6 | `frmProductPrices` / `frmEditPrice` | dialog | A | Edit cost, 3-tier prices, reorder level (Candid also writes `PriceChanges`) | `Products`, `PriceChanges` | `PriceBook.Apply` |
| 7 | `frmSerials` | dialog | A | Serial-number receive / lookup history / write-off / discrepancy report for `TracksSerial` products | `ProductSerials` | 0 rows today; feature exists |
| 8 | `ucSuppliers` (Suppliers) | Inventory ▸ Suppliers | A | Supplier list + AP total; add/edit/delete | `Suppliers` | |
| 9 | `ucSuppliers(purchaseOrdersOnly)` (Purchases) | Inventory ▸ Purchases | A | PO list; **Receive**, **Mark paid** | `PurchaseOrders`, `PurchaseOrderItems`, `StockBatches`, `Ledger` | |
| 10 | `frmNewPO` | dialog | A | New PO: supplier, lines, VAT, *receive now* (Candid), warehouse, pay-now (old debt applied oldest-first), previous-balance prompt (`frmPreviousSupplierDebt`) | `Purchasing.Save` | |
| 11 | `ucCustomers` | Customers | A | Customer list ranked by 12-month spend (Gold/Silver/Bronze), AR total, rebate total, add/edit/delete, **record payment** (`frmRecordPayment`), metrics (`frmCustomerMetrics`) | `Customers`, `Invoices`, `Payments`, `Ledger`, `vw_CustomerRebate` | |
| 12 | `ucInvoices` (Sales / Receipts) | Customers ▸ Sales, Receipts | A+C | Invoice list, search, scan-receipt, receipt preview/PDF, CSV export. **Admin:** delete invoice, "Est. profit" column, price-override history | `Invoices`, `InvoiceItems`, `PriceOverrides` | |
| 13 | `frmNewInvoice` (+`frmInvoiceReceipt`, `frmPreviousDebt`, `frmEditPrice`) | dialog | A+C | POS: customer (inline create), price tier, warehouse, lines (barcode scan), discount %, VAT, payment method, pay-now, due date, prior-debt prompt, stock-shortfall advice, manual price override (logged), load-from-quotation | `Sales.Save` | Core workflow |
| 14 | `ucQuotations` + `frmNewQuotation` | Customers ▸ Quotations | A+C | Priced quote; no effect on stock/ledger/balance; convert → real sale; admin-only delete + price-override history | `Quotations`, `QuotationItems` | |
| 15 | `ucWaybill` + `frmNewWaybill` | Customers ▸ Waybill | A+C | Delivery note per invoice (driver, phone, plate, destination); print with company dispatch stamp | `Waybills` | Recent commit adds dispatch stamps |
| 16 | `ucPriceList`, `frmPriceUpdate`, `frmAddPriceListProducts`, `frmSendPriceList` | Price list (Candid only) | A+C view/send; A edit | Current prices & change history; bulk % update with rounding; add products in bulk; generate/email price list (mailto) | `Products`, `PriceChanges` | |
| 17 | `ucFinance` | Finance | A | Revenue/COGS/gross profit, month expenses, AP, AR, ledger grid (delete entry), export | `vw_ProfitAndLoss`, `Expenses`, `vw_AccountsPayable`, `Customers`, `Ledger` | |
| 18 | `ucExpenses` + `frmAddExpense` | Finance ▸ Expenses | A | Expense CRUD (Rent, Salaries, Utilities, Logistics, Maintenance, Other) | `Expenses` | |
| 19 | `ucIncome` (+`Exporter.RunPeriodReport`) | Finance ▸ Income | A | Monthly income (gross, VAT, net, collected, outstanding), period report → Excel/PDF | `vw_MonthlyIncome`, `Invoices`… | |
| 20 | `ucRebates` | Finance ▸ Rebates | A | Rebate accrued/redeemed per customer, redeem, set default rate | `RebateEntries`, `vw_CustomerRebate`, `AppSettings` | |
| 21 | `ucEmployees` (+`frmAddEmployee`, `frmPayrollHistory`) | Employees | A | Roster, monthly payroll generation, edit salary/loan deduction, mark paid (posts `Expenses`), loans + repayments, history | `Employees`, `EmployeeMonthly`, `EmployeeLoans`, `LoanRepayments`, `Expenses` | `Payroll.MarkPaid` |
| 22 | `ucAIAssistant` | AI Assistant | A | Chat over pre-canned summary queries via local Ollama | views | Role text says clerks must not see profit (moot: A only) |
| 23 | `frmLogin`, `frmRegister`, `frmSetPassword` | auth | – | Company picker, sign-in, first-admin creation, forced/normal password change | `Users`, `LoginAudit` | |
| 24 | Account menu (`frmMain.BuildAccountMenu`) | – | A (except change-password) | Change password (all); add user, backup .bak, export all data (xlsx), storage/archive, licence info (Admin) | – | |
| 25 | `frmClerkWelcome` + `Attendance` | on clerk login | Clerk | Welcome clip + check-in card; check-out on sign-out/close. Append-only `AttendanceEvents` (In / Out / Declined) | `AttendanceEvents` | Clerks can *decline* check-in, which signs them out |
| 26 | `ucStaff` | **not reachable** | – | Staff accounts grid (disable/delete) — **not wired into `ShowScreen`**; only Add exists via account menu. Nothing in the UI can disable or delete a user today | `Users` | **[CONFIRM]** the web app should offer full user management |
| 27 | `frmActivation`, `frmIdleWarning`, `frmAppearance`, `frmArchive`, `frmCustomerMetrics` | misc dialogs | | Licence, idle warning, theme, archive, customer metrics | | |

### Role matrix as actually implemented (extracted, not assumed)

**Admin:** everything above.
**Warehouse Clerk:** Dashboard (no profit/advice); Inventory (view stock/batches/history, add item, record production or purchase, move stock, print label, export; *not* edit prices/serials/delete); Sales, Quotations, Receipts, Waybill; Price list view + send (Candid); change own password; attendance check-in.
**Clerk cannot:** Customers screen, Suppliers, Purchases, Finance, Expenses, Income, Rebates, Employees, AI; delete invoices/products/production entries; see estimated profit; add users; backup/export/archive.
**Idle logout:** Admin sessions only (5 min idle, 3 min warning). Clerks never idle out.
**Lockout:** 5 failed attempts → locked 15 min (`Security.vb`). Password policy: ≥8 chars, letters+digits, no leading/trailing space, blocklist (`password`, `12345678`, `qwerty`, `admin123`, `chewypets`).

> Note: a clerk can create a **customer** inline while making a sale/quotation (`frmNewInvoice`) even though the Customers screen is admin-only. Preserve or restrict? **[CONFIRM]**

---

## 4. Database inventory

See `DATABASE_MAPPING.md` for column-level detail. Summary:

| Group | Tables |
|---|---|
| Identity | `Roles`, `Users`, `LoginAudit` |
| Reference | `Categories`, `Warehouses`, `AppSettings` (KV) |
| Products & stock | `Products`, `StockBatches`, `StockMovements`, `ProductSerials`, `PriceChanges` (Candid only) |
| Purchasing | `Suppliers`, `PurchaseOrders`, `PurchaseOrderItems` |
| Sales | `Customers`, `Invoices`, `InvoiceItems`, `Payments`, `Quotations`, `QuotationItems`, `Waybills`, `PriceOverrides`, `RebateEntries` |
| Finance | `Ledger`, `Expenses` |
| People | `Employees`, `EmployeeMonthly`, `EmployeeLoans`, `LoanRepayments`, `AttendanceEvents`, *(legacy `Attendance`)* |
| Views (6) | `vw_LowStock`, `vw_StockValuation`, `vw_ProfitAndLoss`, `vw_AccountsPayable`, `vw_CustomerRebate`, `vw_MonthlyIncome` |
| Triggers / stored procs / functions | **None** (0/0/0 verified). All logic is in VB. |

Row counts (local dev copy; **not** production): Chewy — Products 6, Customers 5, Invoices 12, InvoiceItems 12, Payments 9, StockBatches 9, StockMovements 14, RebateEntries 9, LoginAudit 110, AttendanceEvents 38, Users 3. Candid — Products 2, Customers 2, Invoices 7, StockMovements 8, AttendanceEvents 21, Users 2.
Local control totals: 12 invoices, Σ`TotalAmount` = 56,275,185.28, Σ`AmountPaid` = 56,195,185.28, Σ`Customers.Balance` = 195,000.00, Σ`QuantityOnHand` = 1,223, no negative stock, **1 ledger row** whose `Reference` matches neither an invoice nor a PO number (expected: `'Payment'` references).

`AppSettings` keys present: `app.lang`, `backup.lastDate`, `license.*` (4), `rebate.ratePct`, `theme.*` (9). Only `rebate.ratePct` is business data; the rest are per-workstation/desktop concerns.

---

## 5. Business rules (extracted from code)

Numbered for cross-reference in `FEATURE_MAPPING.md`. **[CONFIRM]** = ambiguity.

### Sales (`Sales.vb`)
- **S1** A sale needs ≥1 line. Money: `Subtotal = Σ qty×price`; `Discount = Round(Subtotal × pct/100, 2)`; `VAT = Round((Subtotal − Discount) × rate/100, 2)`; `Total = Subtotal − Discount + VAT`. **`Math.Round` here is banker's rounding (MidpointRounding.ToEven)** in VB.NET/.NET; the port must use the same mode or historical totals will differ by 1 kobo in half-cases. (`PriceBook.Adjusted` is the exception — it explicitly uses `AwayFromZero`.)
- **S2** Default VAT 7.5% (App.config `VatRate`; `Invoices.VATRate` default 7.5; `Quotations.VATRate` default 0); a sale may carry VAT 0.
- **S3** One DB transaction for: invoice header, items, stock deduction, `StockMovements`, customer balance, ledger, payments, rebate, price-override log. All-or-nothing.
- **S4** Stock check runs *first*, under `UPDLOCK, HOLDLOCK`, summed across **all warehouses**; if any product is short, the whole sale is refused with every short line and restock advice (`InsufficientStockException`).
- **S5** Deduction order: chosen warehouse first, then others; within that, earliest expiry first (NULL expiry last), then `BatchID` (FEFO). A line may split across batches/warehouses. `QuantityOnHand >= take` guard on the UPDATE. Each split writes an `OUT` movement against the warehouse it actually left.
- **S6** Customer's existing debt is read under lock. Payment `PaidNow` is capped at `prevBalance + total`; applied to **this invoice first**, overflow to older unpaid invoices **oldest first** (`ApplyPaymentToOutstandingInvoices`), each capped at what it owes; one `Payments` row per touched invoice.
- **S7** Invoice status: `Paid` if applied ≥ total, `Partial` if > 0, else `Unpaid`. `DueDate` stored only when something is outstanding.
- **S8** `Customers.Balance += total − paidNow`; ledger: `Customer/Credit` = amount applied to old debt, `Customer/Debit` = this invoice's outstanding. Ledger `Reference` = invoice number.
- **S9** **Rebate**: for every customer whose type ≠ `Walk-in`, accrue `Round((Total − VAT) × RebateRatePct/100, 2)` (`Accrued`). Schema comments and README say "credit customers" but the code accrues for **every non-walk-in sale** regardless of payment method. **[CONFIRM]** which is intended.
- **S10** Manual price override: if a line's price ≠ the product's current price for the invoice's tier, log to `PriceOverrides` (denormalized: doc number, product name, standard/override price, user's full name). No FKs by design, so it survives invoice deletion.
- **S11** Price tier: `Distributor | Wholesaler | Retailer` (→ `PriceDistributor/PriceWholesaler/PriceRetail`); `PriceLists.TierFor(customerType)` maps customer type→tier; Walk-in → Retailer. `UnitCost` is captured on each line at sale time for exact COGS.
- **S12** Document number = `<Prefix>-<ddMMyyyy>-<HHmmss>` of *creation time* (not the sale date). Prefix per company (`ChewyStock`, `CandidPurrfect`). Collision → wait 1 s, retry up to 4 times. **`CandidPurrfect-14092026-143205` is exactly 30 chars — the full width of `Ledger.Reference NVARCHAR(30)`; any suffix would truncate.** The web version must widen this column and stop using sleep-retry (see MIGRATION_PLAN).
- **S13** Serial-tracked products: `Serials.ConsumeForSale` marks a serial `Sold` with `InvoiceID`, `SoldAt`. **[CONFIRM]** how the sale UI picks serials (not fully traced).

### Purchasing (`Purchasing.vb`)
- **P1** Totals: `VAT = Round(Subtotal × rate/100, 2)`; `Total = Subtotal + VAT`.
- **P2** Mirror of S6: supplier balance read under lock; paid-now applied to this PO first, overflow to older POs oldest-first; `Suppliers.Balance += total − paidNow`.
- **P3** Ledger polarity is reversed for suppliers: `Supplier/Credit` = we owe more (outstanding of this PO); `Supplier/Debit` = we paid (applied to old debt). A fully-paid PO posts no credit.
- **P4** `ReceiveNow` (Candid): in the same transaction, each line goes to a batch **named after the PO number** (`ReceiveInto`), `IN` movement written, `Products.CostPrice` := line unit cost when > 0, blank barcode := minted internal barcode, PO status → `Received`. Otherwise PO is `Pending`.
- **P5** "Mark paid" (`ucSuppliers.btnMarkPaid`): under lock, pays whatever remains on that one PO, reduces `Suppliers.Balance` by that amount, posts `Supplier/Debit`.
- **P6** PO statuses in schema: Pending, Ordered, Received, Cancelled; only Pending→Received is implemented. Payment: Paid/Partial/Unpaid.

### Stock (`Stock.vb`, `Serials.vb`)
- **T1** Production run: tops up batch `(product, warehouse, batchNumber)` or creates it (default batch name `PROD-yyMMdd`); `IN` movement, `ReferenceType='Production'`, `ReferenceID=BatchID`.
- **T2** Correct production: quantity/date change moves stock by the *delta*; reductions come from the entry's own batch, then the day's `PROD-` batch, then newest other batches in that warehouse; **refused if not enough is left** (already sold/moved). New quantity 0 **deletes the movement row**.
- **T3** Transfer: same-product, two different warehouses; earliest-expiry moved first; batch number + expiry preserved at destination; one `OUT` and one `IN` movement, `ReferenceType='Transfer'`, `ReferenceID` = other warehouse.
- **T4** Restock advice (`AdviseRestock`): demand = Σ `OUT` movements (excluding transfers) in trailing **90 days** ÷ 90; lead time 7 d; cover 30 d; reorder point = `max(ceil(perDay × 10.5), ReorderLevel)`; suggested = `max(ceil(perDay × 37) , reorderPoint) − onHand` (min 1 if at/under reorder point). Urgency: *Out of stock / Reorder now / Low stock / OK*.
- **T5** Stock status view: `Low stock` if `QuantityOnHand <= ReorderLevel` (**per batch row**, not per product); `Expiring soon` if expiry ≤ 60 days. Dashboard counts only `Low stock`.
- **T6** `MovementType` ∈ {`IN`,`OUT`,`ADJUST`}; **`ADJUST` is never written anywhere** — there is no stock-adjustment/count feature today. The brief's `/inventory/adjust` and `ADJUSTMENT` type would be *new* functionality. **[CONFIRM]**
- **T7** Serial units: statuses `In Stock, Sold, Returned, Written Off`; `Discrepancies()` compares batch total to registered serial count.

### Customers / debt / payments
- **C1** `CustomerType`: Distributor, Wholesaler, Retailer, Walk-in. `RebateRatePct` default 1.0. `CreditLimit` is stored and editable; **it is not enforced** anywhere I found (sales are not blocked over the limit). **[CONFIRM]**
- **C2** "Record payment" (`ucCustomers`): in one transaction, applies oldest-first (`ApplyPaymentToOutstandingInvoices`), reduces `Customers.Balance`, posts `Customer/Credit`, reference `'Payment'`.
- **C3** Customer ranking by trailing-12-month spend: Gold/Silver/Bronze tiers: trailing-12-month total ≥ ₦800,000 → Gold, ≥ ₦400,000 → Silver, else Bronze (hard-coded in `ucCustomers.vb:86`). These are for year-end incentives and are **not** the same as `CustomerType`.

### Quotations
- **Q1** No stock/ledger/balance effect. Status `Open` → `Converted` (with `ConvertedInvoiceID`). Editable/deletable only while Open. Clerks can create, view and convert; **only Admin can delete** a quotation or view the price-override history. Convert = open the sale form pre-filled, then ordinary `Sales.Save`, then `Quotations.ConvertToSale`. These are **two separate steps, not one transaction** (if the second fails, the quote stays Open while the invoice exists). Price overrides are also logged for quotations.

### Payroll (`Payroll.vb`, `ucEmployees`)
- **R1** "Generate month": for each active employee with no row for that period: salary = their most recent `EmployeeMonthly.SalaryAmount`, else `Employees.MonthlySalary`; loan deduction = `min(Σ open loan balances, Round(Σ open loan balances / 6, 2))` (i.e. repay over six months). Existing rows are skipped, so it is re-runnable.
- **R2** `MarkPaid`: one transaction — guarded `UPDATE … WHERE Paid = 0` (a concurrent second click loses the race and reports "already paid"), then applies the loan deduction to open loans **oldest first** (`LoanRepayments`, decrement `Balance`, set `Closed` at ≤ 0), then posts an `Expenses` row (`Salaries`) for **net pay** (`Salary − LoanDeduction`), dated *today* (not the payroll period), noted `"<name> — <period>"`. Expense therefore excludes the amount recovered from a loan. **[CONFIRM]** that net-pay expensing is intended.
- **R3** `Staff.DeleteEmployee` hard-deletes an employee with all payroll and loans.

### Security / accounts
- **A1** PBKDF2-HMAC-SHA256, 120 000 iterations, 16-byte salt, 32-byte hash, stored `pbkdf2$iter$salt$hash`. Constant-time compare. **Not compatible with ASP.NET Identity's format** — but can be *verified* by a custom `IPasswordHasher` so existing users keep their passwords (see MIGRATION_PLAN). Verification of legacy hashes should be retained then re-hashed on first successful login.
- **A2** Accounts with placeholder hash (`SETUP_REQUIRED`) or `MustChangePassword=1` are forced to set a password at sign-in. The seed users `ifeoma.c` (Admin) and `david.o` (Clerk) carry that placeholder.
- **A3** `HasUsableAdmin`: if no active Admin has a real hash, the app runs "create first admin". Unactivated licence: modules locked, sessions capped at 10 minutes (desktop licensing only).
- **A4** Every login attempt (success/fail/reason/machine name) is written to `LoginAudit`. There is **no** audit of business actions besides `PriceOverrides`, `PriceChanges`, `StockMovements.UserID` and `CreatedByUserID` columns.

### Reporting
- **X1** Period report (`Exporter.BuildReport`): GrossSales, VAT, Collected, Outstanding, COGS, Expenses, RebateAccrued for a year or month, plus invoice list, expenses, rebates and payroll sheets → Excel (ClosedXML) or PDF preview.
- **X2** Profit: Revenue = Σ`LineTotal` (ex-VAT, **before** invoice-level discount — see defects), COGS = Σ`qty×UnitCost`. `vw_ProfitAndLoss` ignores `Invoices.DiscountAmount`.
- **X3** "Est. profit" per invoice (admin) = `Total/(1+VATRate/100) − Σ qty×UnitCost` (this *does* include the discount, so it disagrees with X2 when a discount is used).
- **X4** Full export: every table as one workbook (also used as manual backup). Receipts/waybills/quotations/price lists are drawn by `DocPrinter` (letterhead, panels, table, totals, stamp/signature, Code128/QR) → print or PDF.

---

## 6. Existing defects / inconsistencies found

These matter because **porting faithfully copies bugs**. Each needs a decision (keep / fix / fix with migration). None has been "fixed" in any way.

| ID | Where | Finding | Risk |
|---|---|---|---|
| **D1** | `ucInvoices.btnDelete_Click` | Deleting an invoice removes payments, items, waybills, rebates and ledger rows **but does not restore stock, does not reverse `Customers.Balance`, does not release serials** (UI text admits only the stock part). Also fails with an FK error if the invoice was converted from a quotation (`Quotations.ConvertedInvoiceID`) or a serial references it. | Balance and stock silently drift; violates the brief's "no stock outside transactions/history" rule |
| **D2** | `ucSuppliers.btnReceive_Click` | "Receive" a Pending PO: uses **`SELECT TOP 1 WarehouseID`**, writes batch `PO-RECEIPT`, **no `StockMovements` row, no user recorded, no cost-price update, and every statement runs on its own connection (not a transaction)**. Can be double-received (button only disabled client-side). Different from the transactional `ReceiveInto`. | Stock without history; double receipt |
| **D3** | `ucInventory.btnDelete_Click` | Deletes the product's `StockBatches` (except `PO-RECEIPT`) in a *separate, swallowed-error statement* **before** trying to delete the product; if the product delete then fails on an FK, the stock is already gone. Products have `IsActive` but are hard-deleted. | Stock loss |
| **D4** | `frmAddItem` → `ucInventory.btnAddItem_Click` | Opening quantity inserted with **no `StockMovements` row**; product insert, barcode update and batch insert are three separate connections. | Stock without history |
| **D5** | `ucInvoices.MarkInvoicePaid` | Sets `Status='Paid'` without touching `AmountPaid` or `Customers.Balance`. **Dead code** (no caller). | Do not port |
| **D6** | `Stock.CorrectProduction` with qty 0 | Deletes the `StockMovements` history row. | History destruction (admin only) |
| **D7** | `vw_ProfitAndLoss` vs Est. profit | Revenue ignores invoice-level discount; the two profit measures disagree when `DiscountPct > 0`. | Reporting accuracy [CONFIRM which is right] |
| **D8** | `Ledger.Reference NVARCHAR(30)` | Candid document numbers are exactly 30 chars. | Truncation on any change |
| **D9** | Transfer movements | `StockMovements.MovementDate` omitted (defaults to now) while sales write the *sale date* at midnight; movement dates are inconsistent (date vs datetime). | Time-window reports |
| **D10** | Quotation→invoice | Two-step, non-atomic (see Q1). | Orphan state |
| **D11** | Rebate rule | Code vs comment mismatch (S9). | Money |
| **D12** | `CreditLimit` | Stored, never enforced (C1). | Business intent |
| **D13** | `IsSample` | Demo data (`SampleData.vb`) is flagged `IsSample=1`; production DB may contain sample invoices/POs that must be excluded from real totals or purged before go-live. | Wrong totals |
| **D14** | `ucStaff` | Orphaned; no reachable way to disable/delete a user. | Access control |
| **D15** | Timezones | Mixed: `SYSDATETIME()` (server local wall-clock) for `CreatedAt`, `PaymentDate`, `MovementDate`, `ReceivedAt`, `HappenedAt`, `LastLoginAt`… vs `SYSUTCDATETIME()` for `LoginAudit.AtUtc`, `Users.LockedUntil`, `LastLoginAt`. The zone of the desktop PCs is not recorded (business is Nigerian, so presumably WAT / UTC+1). | Off-by-an-hour or wrong-day results after migration [CONFIRM zone] |

---

## 7. Authentication logic summary

Sign-in flow (`frmLogin` → `Auth.TryLogin`): username → lookup in **home DB** → active? → locked? → placeholder hash? → PBKDF2 verify → reset failures + `LastLoginAt` → `MustChangePassword`? → `EnterCompany` (mirror account into chosen company DB, creating + migrating its DB on first use) → clerk welcome/attendance → `frmMain(role)`.
Outcomes: Success, MustChangePassword, BadPassword, UnknownUser, Disabled, LockedOut. The message for unknown user ("Unknown username.") differs from bad password — the web version should use one generic message (user-enumeration).

## 8. External dependencies

| Dependency | Used for | Web plan |
|---|---|---|
| SQL Server LocalDB 2019+ | database | MySQL 8 |
| .NET Framework 4.8 WinForms/GDI+ | UI, printing, image handling | Angular + server-side PDF |
| ClosedXML | .xlsx export | ClosedXML (works on .NET 10) or EPPlus alt; CSV |
| QRCoder | QR tags | QRCoder (works cross-platform) |
| Code128 (hand-written in `Barcodes.vb`) | barcodes | port or JsBarcode client-side |
| System.Management (WMI) | machine ID for licence | dropped with licensing |
| WPF `MediaElement` | clerk welcome video (`Assets/landingvideo.mp4`) | **[CONFIRM]** keep? |
| Ollama (`localhost:11434`) | AI Assistant | no equivalent on VM (see §9-D6) |
| `licensing-api` (Node, render.com, ALATPay payments) | activation / payment | dropped |
| SMTP / `mailto:` | price-list email | `mailto:` link stays; server-side SMTP is new |

## 9. Decisions

**Resolved (2026-09-19):**
* **D1 → one MySQL schema per company** (`chewypets`, `candid`), shared identity store; company resolved per request from the session. Source DBs map 1:1; no ID re-keying of business tables (only `UserID`s are remapped to the merged identity store).
* **D3 → Core release first:** auth + users, products, stock (incl. purchase receipt, production, transfer), purchases + supplier payables, sales + receipts, customers + payments + ledger, income report, dashboard. Quotations, waybills, rebates, expenses, price book, payroll, attendance, serials, analytics, Igbo follow in later releases (`FEATURE_MAPPING.md` R2/R3). AI assistant and licensing dropped from scope.

**Still open** (none block scaffolding; D2, D5, D7 must be answered before Phase 3 / before money rules are coded):

| # | Question | My recommendation |
|---|---|---|
| **D1** | Multi-company: DB-per-company vs single DB with `CompanyId`? | Single DB + `CompanyId` with EF global filters and isolation tests, **or** two MySQL schemas — your call; I lean to the latter only if the two businesses must never share reporting |
| **D2** | Where is the **production** SQL Server data (this machine's copy is dev-sized)? Can I get a `.bak` copy? | Needed before Phase 3 |
| **D3** | Scope: which of {payroll+loans, attendance, serials, price book, quotations, waybills, rebates, forecasting/market-basket/anomaly analytics, Igbo UI, clerk welcome video, AI assistant, archive} are **in** for go-live? | Core first: auth, products, stock, purchases, sales, customers, payments/ledger, reports. Then quotations, waybills, rebates, expenses. Then the rest in later releases |
| **D4** | Licensing ($250) — drop for the hosted product? | Drop |
| **D5** | Fix defects D1–D4, D6, D7, D10, D11, D12 in the port or reproduce? | Fix D1–D4, D6, D10 (integrity); ask on D7, D11, D12 (money rules) |
| **D6** | AI Assistant: Ollama can't sensibly run on 2 OCPU/12 GB ARM alongside the app; drop, or call a hosted model (cost, data leaving the VM)? | Drop from v1 |
| **D7** | Timezone of existing `datetime2` values | Assume Africa/Lagos; store UTC going forward |
| **D8** | Domain name and off-VM backup destination (Oracle VM must not hold the only backup). Oracle Object Storage Always Free (20 GB) is the zero-cost option and fits the "Oracle-only" constraint | Oracle Object Storage |
| **D9** | Is the client comfortable with **one login to two businesses** (switch company after login) rather than choosing company at the login screen? | Company switcher for users who have both |
| **D10** | Clerk inline customer creation; add real user-management UI (D14)? | Allow inline create; add user admin |
