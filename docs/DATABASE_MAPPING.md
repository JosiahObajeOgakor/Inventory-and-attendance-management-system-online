# DATABASE_MAPPING — SQL Server → MySQL 8

Source: `StockDeskDB` (home/ChewyPets) and `CandidPurrfectDB`, verified against the live LocalDB
(no schema drift vs. `Schema.sql` + `Migration_v2/v3.sql` + `DbBootstrap.Migrate()`, except the
legacy `Attendance` table that both DBs still contain).
Target: MySQL 8.0+ (InnoDB, `utf8mb4`).

## 1. Global conversion rules

| SQL Server | MySQL | Rule / caveat |
|---|---|---|
| `INT IDENTITY(1,1) PRIMARY KEY` | `INT NOT NULL AUTO_INCREMENT PRIMARY KEY` | **Import with original IDs** (turn off nothing—MySQL accepts explicit values), then `ALTER TABLE … AUTO_INCREMENT = max+1`. Preserves all FKs and any printed references. |
| `NVARCHAR(n)` | `VARCHAR(n)` | Charset `utf8mb4`. Collation **`utf8mb4_0900_ai_ci`** is accent-*insensitive*; SQL Server's default `SQL_Latin1_General_CP1_CI_AS` is accent-*sensitive* → use **`utf8mb4_0900_as_ci`** to keep `UNIQUE`/`LIKE` behaviour (e.g. `Users.Username`, `SKU`, `Name`). [CONFIRM collation of source DB with `SELECT DATABASEPROPERTYEX(...,'Collation')`.] Note MySQL trims trailing spaces differently for `PAD SPACE`; 0900 collations are `NO PAD`, which matches SQL Server's usual behaviour more closely than the legacy ones. |
| `BIT` | `TINYINT(1)` (EF: `bool`) | |
| `DECIMAL(p,s)` | `DECIMAL(p,s)` | Identical. Money stays `DECIMAL(14,2)` / `(12,2)`; **never** `double`. |
| `DATE` | `DATE` | Unchanged (date-only columns are unaffected by time zones). |
| `DATETIME2` | `DATETIME(6)` | Precision 7 → 6: truncation of the 7th fractional digit is harmless but **must be reported**. **Time zone**: see §3. |
| `DEFAULT SYSDATETIME()` | `DEFAULT (CURRENT_TIMESTAMP(6))` | Set from the *application* (UTC) rather than server time. |
| `DEFAULT CAST(SYSDATETIME() AS DATE)` | `DEFAULT (CURDATE())` | 8.0.13+ expression defaults. Prefer app-supplied values. |
| `AS CAST(HappenedAt AS DATE) PERSISTED` (computed) | ordinary `DATE` column populated by the app | A generated column can't apply a time-zone conversion deterministically (`CONVERT_TZ` is non-deterministic and needs tz tables). |
| Filtered unique index `WHERE Barcode IS NOT NULL` | plain `UNIQUE (Barcode)` | MySQL permits many NULLs in a unique index. **But** the code treats `''` and NULL alike (`Barcode IS NULL OR Barcode = ''`) → migration must convert `''` to `NULL`, else two blank barcodes collide. |
| `CHECK (col IN (...))` | `CHECK (...)` | Enforced from 8.0.16. |
| Reserved/awkward names | – | `[Status]`, `[Address]` are bracketed in T-SQL; in MySQL neither is reserved (EF quotes anyway). `Position`, `Name`, `Location`, `Category`, `Note`, `Method`, `Year`, `Month` OK. |
| `uniqueidentifier`, `money`, `varbinary`, `datetime` | **not used** | Verified: no such columns in either DB. |
| Views, stored procs, triggers, functions | 6 views; **0** procs/triggers/functions | Views are re-created (see §4). |

## 2. Table-by-table mapping

Legend: PK = primary key, all `INT AUTO_INCREMENT`. "→ same" = identical name and semantics. `?` = nullable.

### Identity

| SQL Server | MySQL | Columns / notes |
|---|---|---|
| `Roles` (2 rows) | `roles` → same | `RoleID` PK, `RoleName VARCHAR(50) UNIQUE`. Values `Admin`, `Warehouse Clerk`. Target may use ASP.NET Identity roles `ADMIN`/`CLERK`; keep a mapping table in the migration report. |
| `Users` | `users` (**or** `AspNetUsers` with extra columns) | `UserID`, `FullName VARCHAR(100)`, `Username VARCHAR(50) UNIQUE`, `PasswordHash VARCHAR(256)` (PBKDF2 string `pbkdf2$120000$…`), `RoleID FK`, `IsActive`, `FailedAttempts`, `LockedUntil DATETIME2?` (**UTC**), `MustChangePassword`, `LastLoginAt?` (**UTC**), `CreatedAt` (**local**). Placeholder hashes `SETUP_REQUIRED` / `CHANGE_ME_…` must be imported as-is and force a password set. |
| `LoginAudit` | `login_audit` | `LoginAuditID`, `Username`, `Succeeded`, `Reason?`, `MachineName?`, `AtUtc` (UTC). Machine name is meaningless on the web — keep for history; new rows store client IP instead. |

### Reference

| `Categories` | `categories` | `CategoryID`, `Name VARCHAR(60) UNIQUE`. |
|---|---|---|
| `Warehouses` | `warehouses` | `WarehouseID`, `Name VARCHAR(80) UNIQUE`, `Location VARCHAR(150)?`. Chewy seed: *Lawal warehouse*, *Shore warehouse*. Candid: *Main store* (after starter cleanup). |
| `AppSettings` | `app_settings` | `SettingKey VARCHAR(80) PK`, `SettingValue VARCHAR(400)?`. **Only `rebate.ratePct` is business data.** `theme.*`, `app.lang`, `backup.lastDate`, `license.*` are **not migrated** (per-desktop). Web app moves per-company settings (VAT rate, rebate default, bank accounts, address/phone/TIN — today in App.config) into a `company_settings` table. |

### Products & stock

| SQL Server | MySQL | Notes |
|---|---|---|
| `Products` | `products` | `ProductID`, `SKU VARCHAR(30) UNIQUE`, `Name VARCHAR(150)`, `CategoryID FK`, `Unit VARCHAR(20) DEFAULT 'Bag'`, `ReorderLevel INT`, `CostPrice`, `SellingPrice` (**legacy; kept = PriceRetail** — keep for fidelity or drop after verifying equality), `PriceDistributor/Wholesaler/Retail DECIMAL(12,2)`, `Barcode VARCHAR(64)? UNIQUE`, `TracksSerial`, `IsActive`. `''`→NULL barcode. |
| `StockBatches` | `stock_batches` | `BatchID`, `ProductID FK`, `WarehouseID FK`, `BatchNumber VARCHAR(40)`, `ExpiryDate DATE?`, `QuantityOnHand INT`. `UNIQUE(ProductID, WarehouseID, BatchNumber)`; index on `ProductID`. **Add `CHECK (QuantityOnHand >= 0)`** (not present today; verified no negatives locally) and a row-version/concurrency token. |
| `StockMovements` | `stock_movements` | `MovementID`, `ProductID FK`, `WarehouseID FK`, `MovementType VARCHAR(10) CHECK IN ('IN','OUT','ADJUST')`, `Quantity`, `ReferenceType VARCHAR(20)?` (`Invoice`,`PurchaseOrder`,`Production`,`Transfer`,`Manual`), `ReferenceID INT?` (**polymorphic**: invoice id / PO id / batch id / other-warehouse id — no FK possible), `MovementDate DATETIME2` (mixed date-at-midnight and now), `UserID FK`. This is the existing stock history; keep it and extend (see FEATURE_MAPPING). |
| `ProductSerials` | `product_serials` | `SerialID`, `ProductID FK`, `SerialNumber VARCHAR(80)`, `BatchID FK?`, `WarehouseID FK?`, `Status CHECK IN ('In Stock','Sold','Returned','Written Off')`, `ReceivedAt`, `SoldAt?`, `InvoiceID FK?`, `Notes?`. `UNIQUE(ProductID, SerialNumber)`; index `(ProductID, Status)`. |
| `PriceChanges` (Candid only) | `price_changes` | `ChangeID`, `ProductID FK`, Old/New × Distributor/Wholesaler/Retail `DECIMAL(12,2)`, `ChangedAt`, `ChangedByUserID FK?`, `Note?`; index `(ProductID, ChangedAt)`. Exists only in `CandidPurrfectDB`. |

### Purchasing

| `Suppliers` | `suppliers` | `SupplierID`, `Name VARCHAR(150)`, `Category?`, `ContactName?`, `Phone?`, `Email?`, `Address?`, `TaxID?`, **`Balance DECIMAL(14,2)`** (maintained running total; not derivable without scanning—verify `Balance == Σ(Total−AmountPaid) of unpaid POs` in the migration report). |
|---|---|---|
| `PurchaseOrders` | `purchase_orders` | `POID`, `PONumber VARCHAR(40) UNIQUE`, `SupplierID FK`, `OrderDate DATE`, `Status`, `PaymentStatus`, `TotalAmount`, `AmountPaid`, `CreatedByUserID FK`, `IsSample`. |
| `PurchaseOrderItems` | `purchase_order_items` | `POItemID`, `POID FK`, `ProductID FK`, `Quantity`, `UnitCost`. |

### Sales

| `Customers` | `customers` | `CustomerID`, `Name`, `ContactName?`, `Phone?`, `Location?`, `Address?`, `Email?`, `CustomerType` (`Distributor/Wholesaler/Retailer/Walk-in`), `TaxID?`, `RebateRatePct DECIMAL(5,2) DEFAULT 1.0`, `CreditLimit`, **`Balance`** (maintained AR total; verify = Σ unpaid invoices). |
|---|---|---|
| `Invoices` | `invoices` | `InvoiceID`, `InvoiceNumber VARCHAR(40) UNIQUE`, `CustomerID FK`, `InvoiceDate DATE`, `Subtotal`, `DiscountPct DECIMAL(5,2)`, `DiscountAmount`, `VATRate DECIMAL(5,2) DEFAULT 7.5`, `VATAmount`, `TotalAmount`, `PaymentMethod` (`Cash/Bank Transfer/Card/Credit`), `Status` (`Paid/Partial/Unpaid`), `AmountPaid`, `DueDate?`, `PriceTier`, `WarehouseID FK?`, `CreatedByUserID FK`, `CreatedAt`, `IsSample`. Indexes on `CustomerID`, `InvoiceDate`. |
| `InvoiceItems` | `invoice_items` | `InvoiceItemID`, `InvoiceID FK`, `ProductID FK`, `Quantity`, `UnitPrice`, `UnitCost` (COGS snapshot), `LineTotal`. Index `InvoiceID`. |
| `Payments` | `payments` | `PaymentID`, `InvoiceID FK`, `PaymentDate DATETIME2`, `Amount`, `Method`, `ReceivedByUserID FK`. |
| `Quotations` / `QuotationItems` | `quotations` / `quotation_items` | as source; `ConvertedInvoiceID FK? → invoices`, `Status` (`Open/Converted`). |
| `Waybills` | `waybills` | `WaybillID`, `WaybillNumber VARCHAR(40) UNIQUE`, `InvoiceID FK`, `IssueDate DATE`, `DriverName?`, `DriverPhone?`, `VehiclePlate?`, `DestinationAddress?`, `Notes?`, `CreatedByUserID INT?` (**no FK in source** — orphans possible; migration must tolerate/report). |
| `PriceOverrides` | `price_overrides` | `OverrideID`, `DocType CHECK IN ('Invoice','Quotation')`, `DocNumber`, `ProductName`, `StandardPrice`, `OverridePrice`, `ChangedByName`, `ChangedAt`. **Deliberately no FKs**; keep denormalised. |
| `RebateEntries` | `rebate_entries` | `RebateEntryID`, `CustomerID FK`, `InvoiceID FK?` (set to NULL by archive), `EntryDate DATE`, `Amount`, `Status` (`Accrued/Redeemed`), `RedeemedDate?`, `Note?`. |

### Finance

| `Ledger` | `ledger` | `LedgerID`, `EntryDate DATE`, `AccountType` (`Customer/Supplier`), `AccountName VARCHAR(150)` (**name, not FK** — renaming a customer orphans history), `EntryType` (`Debit/Credit`), `Amount`, **`Reference VARCHAR(30)? → widen to VARCHAR(64)`** (Candid numbers are 30 chars; 'Payment' also used). Recommended new columns: `CustomerId?`/`SupplierId?` filled by migration by name-match where unambiguous (report the rest). |
|---|---|---|
| `Expenses` | `expenses` | `ExpenseID`, `Category VARCHAR(50)`, `ExpenseDate DATE`, `Amount`, `Note?`, `CreatedByUserID FK`. |

### People

| `Employees` | `employees` | `EmployeeID`, `FullName VARCHAR(120)`, `Position`, `Phone?`, `StartedOn DATE`, `MonthlySalary`, `IsActive`. |
|---|---|---|
| `EmployeeMonthly` | `employee_monthly` | `UNIQUE(EmployeeID, PeriodYear, PeriodMonth)`; `Paid`, `PaidDate?`. |
| `EmployeeLoans` / `LoanRepayments` | same | as source. |
| `AttendanceEvents` | `attendance_events` | `EventID`, `UserID FK`, `FullName`, `EventType CHECK IN ('In','Out','Declined')`, `HappenedAt DATETIME2`, `WorkDate DATE` (was computed). Indexes `(UserID, WorkDate)`, `(WorkDate)`. |
| `Attendance` (legacy) | **not migrated** | Superseded by `AttendanceEvents` (`DbBootstrap.Migrate` already copied its rows). Verify counts before dropping; report. |

## 3. Time-zone handling (must be decided; D15 in DISCOVERY)

| Kind | Columns | Rule |
|---|---|---|
| Date-only | all `DATE` columns | copy verbatim |
| Server-local wall clock (`SYSDATETIME`) | `Users.CreatedAt`, `Invoices.CreatedAt`, `Payments.PaymentDate`, `StockMovements.MovementDate`, `ProductSerials.ReceivedAt/SoldAt`, `PriceChanges.ChangedAt`, `PriceOverrides.ChangedAt`, `AttendanceEvents.HappenedAt`, `Users.LastLoginAt`(*mixed*) | interpret as **Africa/Lagos** (UTC+1, no DST) **[CONFIRM]** and store UTC (`−1h`) — or keep wall-clock; either way recorded in the report |
| UTC | `LoginAudit.AtUtc`, `Users.LockedUntil`, `Users.LastLoginAt` (set via `SYSUTCDATETIME()` in `Auth.vb`) | copy verbatim |
| `AttendanceEvents.WorkDate` | derived | recompute as the *Lagos-local* date of `HappenedAt` |

## 4. Views → MySQL

| View | T-SQL construct that breaks | MySQL rewrite |
|---|---|---|
| `vw_LowStock` | `DATEDIFF(DAY, GETDATE(), sb.ExpiryDate) <= 60` | `DATEDIFF(sb.ExpiryDate, CURDATE()) <= 60` — **argument order is reversed** in MySQL and there is no `DAY` unit |
| `vw_StockValuation` | none | direct |
| `vw_ProfitAndLoss` | `YEAR()/MONTH()` fine | direct. (Consider replacing views with query-side EF projections; they also bake in defect D7 — revenue ignores invoice discount.) |
| `vw_AccountsPayable` | none | direct |
| `vw_CustomerRebate` | `ISNULL(SUM(CASE…),0)`, `[Status]` | `COALESCE(SUM(CASE…),0)` |
| `vw_MonthlyIncome` | `COUNT(DISTINCT …)` fine | direct |

Recommendation: implement these as application queries (EF Core LINQ / parameterised SQL) rather than MySQL views, so they are unit-testable; keep view DDL only if you want ad-hoc SQL access.

## 5. T-SQL constructs in the application code that have no direct MySQL equivalent

| T-SQL (where) | Purpose | Replacement |
|---|---|---|
| `WITH (UPDLOCK, HOLDLOCK)` (Sales, Purchasing, Stock, PriceBook) | pessimistic row/range locking | InnoDB `SELECT … FOR UPDATE` inside a transaction (EF Core: raw SQL `FromSqlRaw("… FOR UPDATE")` or `ExecuteUpdate` atomic guarded UPDATEs). `REPEATABLE READ` uses gap locks — behaviour differs from `HOLDLOCK` (SERIALIZABLE range lock); use ordered locking (lock products by id ascending) to avoid deadlocks, and retry on error 1213. |
| `SCOPE_IDENTITY()` | new id | EF Core populates keys |
| `IF @@ROWCOUNT = 0 INSERT …` (upsert batch) | upsert | `INSERT … ON DUPLICATE KEY UPDATE` (unique key `(ProductID,WarehouseID,BatchNumber)`) or EF find-then-add under lock |
| `MERGE` (Auth.MirrorAccount, AppSettings) | upsert | `ON DUPLICATE KEY UPDATE` |
| `TOP n` / `SELECT TOP 1 … ORDER BY` | limit | `LIMIT n` |
| `ISNULL(a,b)` | coalesce | `COALESCE` / `IFNULL` |
| `DATEADD(DAY, -@n, SYSDATETIME())` | date math | `DATE_SUB(UTC_TIMESTAMP(), INTERVAL n DAY)` or compute in C# |
| `DATENAME(MONTH, d)`, `DATEPART(week, d)` | reports/forecast | `MONTHNAME`, `YEARWEEK`/`WEEK(d, mode)` — **week numbering differs by mode**; compute weeks in C# for the forecasting code |
| `'a' + CAST(YEAR(d) AS VARCHAR)` | concat | `CONCAT()` (with `+`, MySQL does numeric addition and returns 0!) |
| `WHERE (@s IS NULL OR col LIKE @s)` | optional filters | fine; EF translates `Contains` |
| Error numbers `2627`, `2601` (duplicate), `547` (FK) | flow control (`Numbering.IsDuplicate`, `CompanyData.RemoveEach`) | MySQL `1062` (duplicate), `1451/1452` (FK); catch as `MySqlException` / `DbUpdateException` |
| `DBCC CHECKIDENT … RESEED`, `FILEPROPERTY`, `BACKUP DATABASE … TO DISK`, `SqlLocalDB.exe` | engine admin | not applicable — `mysqldump`/Percona XtraBackup, table-size via `information_schema` |
| `CAST(GETDATE() AS DATE)` | today | `CURDATE()` — careful: use *Lagos* "today", not server (UTC) today, for "today's invoices" |
| Integer / decimal division `/100.0` | rates | same; do rate math in C# `decimal` |

## 6. Referential integrity notes

* All FKs are `NO ACTION` (default) in the source, so deletes are blocked when children exist. Keep `RESTRICT` in MySQL; do not add cascades.
* `Waybills.CreatedByUserID` — no FK. `PriceOverrides` — no FK by design. `Ledger.AccountName` — string link. `StockMovements.ReferenceID` — polymorphic. Each is a place the migration verifier reports rather than fails.
* FK order for import (parents first): `Roles → Users → Categories → Warehouses → Suppliers → Customers → Products → StockBatches → PurchaseOrders → PurchaseOrderItems → Invoices → InvoiceItems → Payments → Quotations → QuotationItems → Waybills → ProductSerials → RebateEntries → StockMovements → Ledger → Expenses → Employees → EmployeeMonthly → EmployeeLoans → LoanRepayments → AttendanceEvents → PriceOverrides → PriceChanges → LoginAudit`.
  (`Quotations.ConvertedInvoiceID` requires `Invoices` first; `ProductSerials.InvoiceID` likewise.)

## 7. Target-only additions (not in source; proposed)

| Addition | Why |
|---|---|
| `CHECK (QuantityOnHand >= 0)` on `stock_batches` | brief §11/§12: DB is the last line of defence |
| `RowVersion`/`xmin`-style concurrency token on `products`, `stock_batches`, `customers`, `suppliers` | optimistic concurrency for edits |
| `audit_log(Id, UserId, Action, Entity, EntityId, At, Detail JSON)` | brief §26. Existing accountability is scattered (`CreatedByUserID`, `PriceOverrides`, `LoginAudit`); a single audit table adds "who edited/deleted what" which the desktop app does not record today. |
| `document_sequences(company, kind, date, n)` | collision-free numbering without sleep-retry |
| `company_settings` | replaces App.config address/bank/VAT |
| `Ledger.CustomerId/SupplierId` | replace name-string join |
| `stock_movements` rows for opening balance, PO receipt, adjustments (`ADJUST`), invoice deletion reversals | close defects D1, D2, D4 |
