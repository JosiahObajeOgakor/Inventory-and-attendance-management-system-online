# VB → web parity (audited 2026-09-20, updated after the second build)

Status key: **Done** = same behaviour on the web · **Replaced** = a web-native mechanism does the job · **Dropped** = removed by a decision · **GAP** = not built.
Every one of the 96 root `.vb` files is listed (Insights = the dashboard reorder list and signals, Done; Program = the desktop entry point, replaced by the ASP.NET host). The VB source was deleted on 2026-09-20; recover any of it with `git checkout vb-desktop-final -- <file>`. Nothing marked GAP should be deleted from the repo until it is built or you decide to drop it.

## Screens (`uc*`, `frm*`)
| VB | Web | Status |
|---|---|---|
| frmLogin, Auth, Security | sign-in, lockout, PBKDF2 legacy hashes, forced change | Done |
| frmSetPassword, frmRegister, ucStaff, frmAddStaff | People & access (create, edit, reset, disable) | Done |
| frmMain, Theme, CardPanel, Anim | app shell, per-company tint, new design | Replaced |
| frmIdleWarning, IdleWatcher | admin idle warning + countdown (browser input only; a website cannot watch the whole PC) | Replaced |
| frmClerkWelcome, Attendance | clerk check-in prompt, decline, sign-out check-out, admin report | Done |
| ucDashboard | admin live dashboard + clerk dashboard | Done (redesigned) |
| ucInventory, frmProduction, frmEditProduction, frmMoveStock | Stock: batches, history, production (+correct), transfer, adjust | Done |
| frmAddItem, frmProductPrices, frmPriceUpdate | Products: add/edit; price book bulk edit + % move | Done |
| ucPriceList, PriceBook, frmAddPriceListProducts | Candid price book: grid, history, add products | Done |
| frmSendPriceList, PriceLists | price list PDF + email with preview | Done |
| ucInvoices, frmNewInvoice, Sales, frmEditPrice, PriceOverrides | sales list, POS, price override log, void | Done ("Mark paid" is customer-level payment, see note 1) |
| frmInvoiceReceipt, DocPrinter, Numbering | receipt PDF (watermark, barcode), document numbers | Done |
| frmPreviousDebt | earlier-debt notice + payment waterfall in POS | Done |
| ucCustomers, frmAddCustomer, frmRecordPayment, frmMoneyPrompt | Customers, record payment | Done |
| frmCustomerMetrics | Customers → History dialog | Done (added today) |
| ucSuppliers, frmAddSupplier, frmPreviousSupplierDebt | Suppliers, "we already owe them" on new purchase | Done |
| frmNewPO, Purchasing | Purchases: new, receive, pay | Done |
| ucQuotations, frmNewQuotation, Quotations | quotations, convert to sale, PDF, email | Done |
| ucWaybill, frmNewWaybill | waybills + PDF | Done |
| ucRebates | rebates, redeem, default rate | Done |
| ucExpenses, frmAddExpense | expenses | Done |
| ucFinance, ucIncome | Finance (P&L, ledger, monthly income) | Done |
| ucEmployees, frmAddEmployee, frmPayrollHistory, Payroll, Staff | employees, payroll, loans, history | Done |
| frmSerials, Serials | serial numbers | Done |
| Barcodes | Code 128 on receipts and shelf labels (Label button on Products, added today) | Done — QR "bin cards / serial tags" **GAP** (note 2) |
| Exporter, AppUI (export) | Excel exports on every list + Finance | Done (PDF period report **GAP**, note 3) |
| ucAIAssistant, OllamaClient | OpenAI assistant (records-only, 10/day) | Replaced |
| Company, AppInfo, CompanyData | company profile, two schemas, per-company settings | Done |
| DataAccess, DbBootstrap, AppPaths, CrashLog | EF Core, migrations, server logging | Replaced |
| BackupService | server backups (encrypted, retention, restore test) in `deploy/` | Replaced |
| UiHelpers, PagedGrid | shared Angular components | Replaced |

## Built after the first audit
| VB | Web | Status |
|---|---|---|
| DbMaintenance, frmArchive | *Database storage*: size by table, preview, archive to ZIP (Excel + CSV) then delete in one transaction, downloads | Done (kept-invoice movements are never archived, so they can still be voided) |
| Forecasting, DemandModel (Holt-Winters, differencing, walk-forward model choice with a stated error) | *Analytics → Demand forecast* | Done (tree-ensemble methods replaced by a 4-week average as an extra baseline; results won't match VB to the decimal) |
| Forecasting (Isolation Forest) | *Analytics → Unusual movements*, plus quantity check at the till and cost check on purchase orders | Done |
| MarketBasket (Apriori / FP-Growth) | *Analytics → Sold together*, upsell at the till, missing partner on a PO | Done (Apriori only; FP-Growth gives identical rules) |
| SaleAdvice, PurchaseAdvice | advice panel on the sale screen; "Suggest what to order" + advice on new purchase | Done (logistic-regression stock-out risk replaced by days-of-cover against the delivery time) |
| Learning (k-means customer segment) | shown as a line in till advice | Done |
| Lang (English + Igbo, override file) | language switch in the account menu; catalogue served by the API; drop-in `ig.json` override | Done (Igbo is machine-assisted: needs a native-speaker review) |
| frmAppearance | *Appearance* (colour, text size, density, lines, stripes, bold headers, row numbers, less shadow) | Done (stored per browser) |
| frmClerkWelcome | full-screen welcome with a 5-second clip slot and a Clock in button | Done (clip itself still to be made, see CLERK_WELCOME_VIDEO.md) |
| — (new) | Paystack pay link on every quotation and every invoice with a balance, clickable in the PDF and in the email; webhook records the payment and tells the admin | New |

## Dropped or unneeded
| VB | Status |
|---|---|
| Licensing, frmActivation, `licensing-api/`, `installer/` | **Dropped** (decision: web app, no per-PC install) |
| SampleData | Not needed in production |
| Trees (random forest / boosting internals) | Not ported; see the forecasting row |

## Notes
1. The desktop "Mark paid" on an invoice paid that one invoice. On the web, money is recorded per customer and applied to the oldest invoices first (rule S6). Recording a payment for a specific invoice first is a small addition if you want it.
2. QR codes for bin cards and serial tags are not printed. Serial numbers themselves are tracked.
3. The desktop exported the period report as PDF or Excel. Web has Excel only.

## Data
The core tables were migrated and verified (control totals to the cent). The tool does **not** yet import: quotations, waybills, attendance, employees, payroll, loans, serials, price changes, company profile, rebate settings. Until that runs on the production backup, the VB app is your only check that the two systems agree.
