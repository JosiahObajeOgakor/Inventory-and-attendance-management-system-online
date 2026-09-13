# ChewyPets Feeds — Developer Handoff

Warehouse inventory, invoicing, purchasing, and profitability system for **ChewyPets Feeds**.
Target: installable Windows 10 desktop app, single laptop, no ongoing licensing cost.

## Modules

Inventory · Invoicing · Purchasing (suppliers, POs, accounts payable) · Customers (accounts receivable) ·
**Finance** (revenue, COGS, gross/net profit, operating expenses) · **Insights** (demand forecast, reorder
suggestions, profit-per-product) · Staff & Access.

The brief was explicit that the business must not lose money on supplies, purchases, or credit — Finance
and Insights exist specifically to surface that: profit is computed end-to-end (sale price → cost →
margin → expenses → net), credit exposure is tracked on both sides (AP owed to suppliers, AR owed by
customers), and the reorder logic exists to prevent both stockouts (lost sales) and overstock (tied-up cash).

## Connecting to YOUR SQL Server (do this first)

Open **`App.config`** and edit the `StockDeskDB` connection string — that's the ONLY place the database
location lives; every screen reads through `DataAccess.vb`, which reads that one value.

```xml
<add name="StockDeskDB" providerName="System.Data.SqlClient"
     connectionString="Server=YOUR_SERVER_NAME;Database=StockDeskDB;Integrated Security=True;TrustServerCertificate=True;" />
```

- Windows Authentication (typical for one office PC/server): keep `Integrated Security=True` as above.
- SQL Server login instead: replace with `User Id=YOUR_LOGIN;Password=YOUR_PASSWORD;`.
- Server value: `localhost` (same machine), `localhost\SQLEXPRESS` (a named Express instance), or the other
  PC's name/IP if the SQL Server lives on a different machine on the office network.

**To test it:** run `Schema.sql` once against that server (creates `StockDeskDB` + seed data), then build
and run the app — the login screen has a **"Test database connection"** button that reports success or
the exact error (wrong server name, server not reachable, login failed, etc.) before you even try to sign in.

## Stack & why

- **VB.NET WinForms** targeting **.NET Framework 4.8** — already built into Windows 10, so there's no extra runtime to install on the client's laptop. Open `StockDesk.vbproj` directly in Visual Studio (2022 Community is free) — it's an SDK-style project, so every `.vb` file in the folder is picked up automatically.
- **SQL Server** — point it at whatever you have available to test with (LocalDB, SQL Server Express, or a full instance) via `App.config` above; nothing in the code is LocalDB-specific. For the final client deployment, SQL Server Express (free, up to 10GB/db) bundled into the installer is the usual choice for a single laptop.
- **Installer**: Visual Studio Setup Project or Inno Setup, bundling the SQL Server Express (or LocalDB) redistributable + the compiled app. One `.msi`/`.exe`, double-click install, no internet required at install time.

## Cost

Everything here — IDE, database engine, installer tooling — is free. The only cost is developer time. This keeps the "won't cost to implement" requirement literal: $0 in licensing.

## Project structure (maps 1:1 to the clickable prototype)

| Screen (prototype) | Form | Core tables |
|---|---|---|
| Login | `frmLogin.vb` | `Users`, `Roles` |
| Dashboard | `frmMain.vb` (host) + `ucDashboard.vb` | `Products`, `StockBatches`, `Invoices`, `Customers` |
| Inventory | `ucInventory.vb` + `frmAddItem.vb` | `Products`, `StockBatches`, `Categories`, `Warehouses` |
| Invoicing | `ucInvoices.vb` + `frmNewInvoice.vb` | `Invoices`, `InvoiceItems`, `Payments` |
| Purchasing | `ucSuppliers.vb` + `frmNewPO.vb` | `Suppliers`, `PurchaseOrders`, `PurchaseOrderItems`, `vw_AccountsPayable` |
| Customers | `ucCustomers.vb` | `Customers` |
| Finance | `ucFinance.vb` + `frmAddExpense.vb` | `Expenses`, `vw_ProfitAndLoss`, `vw_AccountsPayable` |
| Insights | `ucInsights.vb` | `InvoiceItems`, `Products` (forecast computed in code, see below) |
| Staff & Access | `ucStaff.vb` + `frmAddStaff.vb` | `Users`, `Roles` |
| AI Assistant | `ucAIAssistant.vb` + `OllamaClient.vb` | reads via existing summary queries/views only — never raw SQL from the model |

`frmMain.vb` is a single shell form with a top nav bar (matches the HTML prototype, not a sidebar) and a content panel that swaps `UserControl`s in — what you validated there maps directly.

## Included in this handoff — full, working source (not a skeleton)

Every form/control below has real controls wired up in code (`UiHelpers.vb` builds the repetitive label+input
rows so each dialog is short) — this is buildable as-is, not designer stubs to fill in:

- `StockDesk.vbproj` — SDK-style project file; open directly in Visual Studio.
- `App.config` — **edit the connection string here** (see top of this doc).
- `Program.vb` — app entry point: shows login, then the main shell.
- `UiHelpers.vb` — shared layout helpers (labeled form rows, OK/Cancel row, grid defaults).
- `Schema.sql` — full database (tables, FKs, indexes, seed data, reporting views). Run once per server.
- `DataAccess.vb` — ADO.NET helper (parameterized queries, a transaction helper, `TestConnection()`).
- `frmLogin.vb` — login + "Test database connection" button.
- `frmMain.vb` — top-nav shell, role-filtered.
- `ucDashboard.vb` — summary cards (Net profit card Admin-only), low-stock list, recent invoices.
- `ucInventory.vb` + `frmAddItem.vb` — inventory grid with low-stock/expiry highlighting, add-item.
- `ucInvoices.vb` + `frmNewInvoice.vb` — POS: invoice list (Est. profit column Admin-only), new-invoice flow (line items, discount, VAT, stock decrement, credit balance + ledger update). Both roles use this.
- `ucSuppliers.vb` + `frmNewPO.vb` — suppliers + AP owed, purchase orders, new-PO, receive (restocks), mark paid.
- `ucCustomers.vb` + `frmAddCustomer.vb` + `frmRecordPayment.vb` + `frmCustomerMetrics.vb` — customer list sorted by 12-month spend with Gold/Silver/Bronze tiers (for year-end incentives), add customer, record payment, per-customer monthly purchase history + top products.
- `ucStaff.vb` + `frmAddStaff.vb` — staff accounts, add/disable, Admin-only.
- `ucFinance.vb` + `frmAddExpense.vb` — revenue/COGS/gross & net profit, expenses, credit & debit ledger, Admin-only.
- `ucInsights.vb` — demand forecast + reorder suggestions (exponential smoothing, see below), Admin-only.
- `OllamaClient.vb` + `ucAIAssistant.vb` — optional local-AI chat (see that section below); not wired into `frmMain`'s nav by default since it needs Ollama installed separately — add a nav entry once you're ready to include it.

## Still needed before you ship this

1. **Password hashing** — `frmLogin.vb` and `frmAddStaff.vb` have `TODO` markers where a real hash (e.g. `BCrypt.Net-Next` via NuGet) must replace the placeholder checks.
2. **Batch/FEFO stock allocation** — `frmNewInvoice.vb` decrements "first batch with enough quantity"; a full build should let the user pick the batch or apply first-expiry-first-out automatically.
3. **Visual polish** — layout uses `TableLayoutPanel`/`FlowLayoutPanel`/`Dock` throughout so it's fully functional and resizes correctly, but nobody has hand-tuned spacing in the VS designer the way you would for a final client-facing pass. Open any form in the designer to nudge it — the code doesn't fight the designer.

## Build order

1. Edit `App.config`'s connection string, run `Schema.sql` against that server, confirm the seed data loads.
2. Open `StockDesk.vbproj` in Visual Studio, Build. Fix any missing-reference errors (should just be `System.Configuration`, already listed in the `.vbproj`).
3. Run — log in as `ifeoma.c` (Admin) or `david.o` (Warehouse Clerk), any password (see password-hashing note above), and click "Test database connection" first if login fails.
4. Walk through each screen against your own data; add password hashing + any layout polish before packaging.
5. Package with a Setup/Inno Setup project; test a clean install on a fresh Windows 10 VM before handing to the client.

## Notes carried over from the prototype

- VAT rate and currency symbol are configurable (kept as a settings row / `App.config`, not hard-coded) — matches the tweakable props in the HTML prototype.
- Roles: **Admin** (full access, incl. Finance/Insights/Staff) and **Warehouse Clerk** (Inventory/Invoicing/Purchasing/Customers only) — enforce this server-side too (query filters), not just by hiding nav buttons.
- Stock status (OK / Low stock / Expiring soon) is computed the same way in `vw_LowStock` as in the prototype: `Qty <= ReorderLevel` → low stock; `ExpiryDate` within 60 days → expiring soon.

## Local AI Assistant (offline, no internet) — yes, this is possible

Short answer: yes. [Ollama](https://ollama.com) runs as a small background service on the same Windows 10
laptop, no internet needed once a model is downloaded, and VB.NET talks to it over `localhost` like any
other local web API. `OllamaClient.vb` + `ucAIAssistant.vb` show the pattern; the HTML prototype's
**AI Assistant** screen mocks the same UX with a rule-based stand-in (since a real model can't run inside
this preview).

**How it works:**
1. Ollama installs once (during app setup, with internet) and runs quietly in the background afterwards.
2. A small model is pulled once (`ollama pull phi3:mini`, ~2.3GB) — pick a *small* model since this laptop
   has no dedicated GPU: `phi3:mini` or `llama3.2:3b` answer in a few seconds on CPU; a 7B+ model will feel
   slow without a GPU. Test on the actual target laptop before committing to a model size.
3. The app does **not** let the model write or run its own SQL against the live database — that's a
   correctness and safety risk (a wrong or destructive query from a hallucinating model). Instead it runs a
   handful of known, pre-written summary queries (today's sales, low stock, this month's P&L, AP total —
   the same queries the Dashboard/Finance/Insights screens already use) and hands the *results* to the model
   as plain-text context. The model only ever reasons over numbers the app already trusts.
4. Role permissions carry through: if a Warehouse Clerk asks about profit, the context passed to the model
   says not to reveal it, so the answer respects the same Admin-only boundary as the Finance/Insights screens.

**Packaging note:** this adds real weight to the installer — Ollama itself plus a model is roughly 2-4GB,
on top of the app and LocalDB. Two ways to hand it to the client: bundle the Ollama installer + model file
in the setup package (larger installer, but the whole thing works with zero internet at the client site), or
have first-run download them once (needs one-time internet, smaller installer). Either way, ongoing cost is
still $0 — Ollama and these models are free and open-source, consistent with the original brief.

**Not included in this handoff** (straightforward extensions once the base app works): streaming the
model's response token-by-token instead of waiting for the full reply; a background health-check that
disables the AI Assistant nav item if Ollama isn't running rather than failing on first question.

## Profitability: how it's tracked

- **Exact COGS/profit** requires `InvoiceItems.UnitCost` to be set to the product's `CostPrice` *at the moment of sale* (not looked up later — cost changes over time). `frmNewInvoice.vb` must be extended to pass this when inserting invoice lines; `vw_ProfitAndLoss` then gives exact monthly revenue/COGS/gross-profit with no estimation.
- **Net profit** = gross profit − `Expenses` for the same period. Report it monthly (`ucFinance.vb`).
- **Accounts Payable** (`vw_AccountsPayable`) and **Accounts Receivable** (`Customers.Balance`) are both surfaced together on the Finance screen so cash-flow risk from either direction is visible in one place.

## Reorder forecasting — why a simple algorithm, not "real" ML

At this scale (2 staff, 5 SKUs, ~20 invoices/day) a trained ML model (regression/neural forecasting)
would add infrastructure cost and complexity with no accuracy benefit — there isn't enough data per SKU
to train on, and it would need retraining/monitoring the client has no one to own. Instead:

- **Exponential smoothing** (`Sₜ = α·xₜ + (1-α)·Sₜ₋₁`, α = 0.4) over ~6 weeks of per-product sales gives a
  next-period demand forecast that reacts to recent trends but isn't thrown off by one noisy week.
  It's ~10 lines of code, runs instantly, and a non-technical owner can sanity-check it by eye.
- **Reorder suggestion** = `forecast × supplier lead time (weeks)` minus current stock on hand.
- **Confidence** = coefficient of variation of recent weekly sales (low variance → "High" confidence).
- This logic can live in `ucInsights.vb` directly (query weekly sales via `SUM(...) GROUP BY DATEPART(week, ...)`,
  then compute smoothing in VB) — no ML library, no Python service, no model files to ship or version.
- If the product catalog grows into the hundreds of SKUs with years of history, *then* revisit a real
  forecasting library — not before; right-sizing the engineering to the business is itself part of
  keeping this "not cost to implement."
