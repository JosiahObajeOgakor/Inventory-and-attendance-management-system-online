using System.Text;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Inventory.MigrationTool;

public sealed class Report
{
    private readonly StringBuilder _sb = new();
    public List<string> Failures { get; } = [];
    public List<string> Warnings { get; } = [];
    public void H(string text) => _sb.AppendLine().AppendLine($"## {text}").AppendLine();
    public void Line(string text) => _sb.AppendLine(text);
    public void Warn(string w) { Warnings.Add(w); Line($"- ⚠ {w}"); }
    public void Fail(string f) { Failures.Add(f); Line($"- ✗ {f}"); }
    public void Check(string what, decimal source, decimal target)
    {
        if (source == target) Line($"| {what} | {source:N2} | {target:N2} | ✓ |");
        else { Failures.Add($"{what}: source {source:N2} ≠ target {target:N2}"); Line($"| {what} | {source:N2} | {target:N2} | ✗ MISMATCH |"); }
    }
    public override string ToString() => _sb.ToString();
}

/// <summary>Everything a company import needs to know that is not in the data itself.</summary>
public sealed record ImportOptions(TimeSpan SourceUtcOffset, bool ExcludeSample);

/// <summary>
/// Copies ONE company's SQL Server database into its MySQL schema, preserving primary keys, and reports every anomaly it sees.
/// Time columns written by the desktop app with SYSDATETIME() are wall-clock in the source PC's zone (assumed Africa/Lagos,
/// UTC+1 — DISCOVERY D15) and are converted to UTC; DATE columns are copied verbatim.
/// </summary>
public sealed class CompanyImporter(SourceReader src, BusinessDbContext target, IReadOnlyDictionary<string, int> userIdByUsername,
    ImportOptions options, Report report)
{
    private DateTime Utc(Row r, string col) => DateTime.SpecifyKind(r.DateTimeRaw(col) - options.SourceUtcOffset, DateTimeKind.Utc);

    // The source database's own UserID → username, then username → the merged identity id.
    private Dictionary<int, string> _sourceUsers = [];

    private int MapUser(int sourceUserId, string context)
    {
        if (_sourceUsers.TryGetValue(sourceUserId, out var name) && userIdByUsername.TryGetValue(name, out var id)) return id;
        report.Fail($"{context}: source user {sourceUserId} ({(_sourceUsers.GetValueOrDefault(sourceUserId) ?? "unknown")}) has no account in the identity store.");
        return 0;
    }

    public async Task RunAsync()
    {
        _sourceUsers = src.Table("Users").ToDictionary(r => r.Int("UserID"), r => r.Str("Username"));
        var sampleInvoices = options.ExcludeSample ? src.Query("SELECT InvoiceID AS id FROM Invoices WHERE IsSample = 1").Select(r => r.Int("id")).ToHashSet() : [];
        var samplePos = options.ExcludeSample ? src.Query("SELECT POID AS id FROM PurchaseOrders WHERE IsSample = 1").Select(r => r.Int("id")).ToHashSet() : [];

        target.ChangeTracker.AutoDetectChangesEnabled = false;

        // ---- reference data ----
        target.Categories.AddRange(src.Table("Categories").Select(r => new Category { Id = r.Int("CategoryID"), Name = r.Str("Name") }));
        target.Warehouses.AddRange(src.Table("Warehouses").Select(r => new Warehouse { Id = r.Int("WarehouseID"), Name = r.Str("Name"), Location = r.StrN("Location") }));
        await target.SaveChangesAsync();

        // ---- products (blank barcode → NULL; the filtered unique index of SQL Server becomes a plain UNIQUE) ----
        var products = src.Table("Products").Select(r => new Product
        {
            Id = r.Int("ProductID"), Sku = r.Str("SKU"), Name = r.Str("Name"), CategoryId = r.Int("CategoryID"), Unit = r.Str("Unit"),
            ReorderLevel = r.Int("ReorderLevel"), CostPrice = r.Dec("CostPrice"), SellingPrice = r.Dec("SellingPrice"),
            PriceDistributor = r.Dec("PriceDistributor"), PriceWholesaler = r.Dec("PriceWholesaler"), PriceRetail = r.Dec("PriceRetail"),
            Barcode = string.IsNullOrWhiteSpace(r.StrN("Barcode")) ? null : r.Str("Barcode").Trim(),
            TracksSerial = r.Bool("TracksSerial"), IsActive = r.Bool("IsActive"),
        }).ToList();
        foreach (var dup in products.Where(p => p.Barcode != null).GroupBy(p => p.Barcode).Where(g => g.Count() > 1))
            report.Fail($"Duplicate barcode {dup.Key} on products {string.Join(",", dup.Select(p => p.Id))}");
        foreach (var p in products.Where(p => p.SellingPrice != p.PriceRetail))
            report.Warn($"Product {p.Id} ({p.Sku}): legacy SellingPrice {p.SellingPrice} ≠ PriceRetail {p.PriceRetail} (kept as-is).");
        target.Products.AddRange(products);
        await target.SaveChangesAsync();

        target.StockBatches.AddRange(src.Table("StockBatches").Select(r => new StockBatch
        {
            Id = r.Int("BatchID"), ProductId = r.Int("ProductID"), WarehouseId = r.Int("WarehouseID"), BatchNumber = r.Str("BatchNumber"),
            ExpiryDate = r.DateN("ExpiryDate"), QuantityOnHand = r.Int("QuantityOnHand"),
        }));
        await target.SaveChangesAsync();

        // ---- partners ----
        target.Suppliers.AddRange(src.Table("Suppliers").Select(r => new Supplier
        {
            Id = r.Int("SupplierID"), Name = r.Str("Name"), Category = r.StrN("Category"), ContactName = r.StrN("ContactName"), Phone = r.StrN("Phone"),
            Email = r.StrN("Email"), Address = r.StrN("Address"), TaxId = r.StrN("TaxID"), Balance = r.Dec("Balance"),
        }));
        target.Customers.AddRange(src.Table("Customers").Select(r => new Customer
        {
            Id = r.Int("CustomerID"), Name = r.Str("Name"), ContactName = r.StrN("ContactName"), Phone = r.StrN("Phone"), Location = r.StrN("Location"),
            Address = r.StrN("Address"), Email = r.StrN("Email"), CustomerType = r.Str("CustomerType"), TaxId = r.StrN("TaxID"),
            RebateRatePct = r.Dec("RebateRatePct"), CreditLimit = r.Dec("CreditLimit"), Balance = r.Dec("Balance"),
        }));
        await target.SaveChangesAsync();

        // ---- purchasing ----
        var poRows = src.Table("PurchaseOrders").Where(r => !samplePos.Contains(r.Int("POID"))).ToList();
        target.PurchaseOrders.AddRange(poRows.Select(r => new PurchaseOrder
        {
            Id = r.Int("POID"), PoNumber = r.Str("PONumber"), SupplierId = r.Int("SupplierID"), OrderDate = r.Date("OrderDate"), Status = r.Str("Status"),
            PaymentStatus = r.Str("PaymentStatus"), TotalAmount = r.Dec("TotalAmount"), AmountPaid = r.Dec("AmountPaid"),
            CreatedByUserId = MapUser(r.Int("CreatedByUserID"), $"PurchaseOrder {r.Int("POID")}"), IsSample = r.Bool("IsSample"),
        }));
        await target.SaveChangesAsync();
        target.PurchaseOrderItems.AddRange(src.Table("PurchaseOrderItems").Where(r => !samplePos.Contains(r.Int("POID"))).Select(r => new PurchaseOrderItem
        {
            Id = r.Int("POItemID"), PurchaseOrderId = r.Int("POID"), ProductId = r.Int("ProductID"), Quantity = r.Int("Quantity"), UnitCost = r.Dec("UnitCost"),
        }));

        // ---- sales ----
        var invRows = src.Table("Invoices").Where(r => !sampleInvoices.Contains(r.Int("InvoiceID"))).ToList();
        target.Invoices.AddRange(invRows.Select(r => new Invoice
        {
            Id = r.Int("InvoiceID"), InvoiceNumber = r.Str("InvoiceNumber"), CustomerId = r.Int("CustomerID"), InvoiceDate = r.Date("InvoiceDate"),
            Subtotal = r.Dec("Subtotal"), DiscountPct = r.Dec("DiscountPct"), DiscountAmount = r.Dec("DiscountAmount"), VatRate = r.Dec("VATRate"),
            VatAmount = r.Dec("VATAmount"), TotalAmount = r.Dec("TotalAmount"), PaymentMethod = r.Str("PaymentMethod"), Status = r.Str("Status"),
            AmountPaid = r.Dec("AmountPaid"), DueDate = r.DateN("DueDate"), PriceTier = r.Str("PriceTier"), WarehouseId = r.IntN("WarehouseID"),
            CreatedByUserId = MapUser(r.Int("CreatedByUserID"), $"Invoice {r.Str("InvoiceNumber")}"), CreatedAt = Utc(r, "CreatedAt"), IsSample = r.Bool("IsSample"),
        }));
        await target.SaveChangesAsync();
        target.InvoiceItems.AddRange(src.Table("InvoiceItems").Where(r => !sampleInvoices.Contains(r.Int("InvoiceID"))).Select(r => new InvoiceItem
        {
            Id = r.Int("InvoiceItemID"), InvoiceId = r.Int("InvoiceID"), ProductId = r.Int("ProductID"), Quantity = r.Int("Quantity"),
            UnitPrice = r.Dec("UnitPrice"), UnitCost = r.Dec("UnitCost"), LineTotal = r.Dec("LineTotal"),
        }));
        target.Payments.AddRange(src.Table("Payments").Where(r => !sampleInvoices.Contains(r.Int("InvoiceID"))).Select(r => new Payment
        {
            Id = r.Int("PaymentID"), InvoiceId = r.Int("InvoiceID"), PaymentDate = Utc(r, "PaymentDate"), Amount = r.Dec("Amount"), Method = r.Str("Method"),
            ReceivedByUserId = MapUser(r.Int("ReceivedByUserID"), $"Payment {r.Int("PaymentID")}"),
        }));
        target.RebateEntries.AddRange(src.Table("RebateEntries").Where(r => r.IntN("InvoiceID") is not int i || !sampleInvoices.Contains(i)).Select(r => new RebateEntry
        {
            Id = r.Int("RebateEntryID"), CustomerId = r.Int("CustomerID"), InvoiceId = r.IntN("InvoiceID"), EntryDate = r.Date("EntryDate"),
            Amount = r.Dec("Amount"), Status = r.Str("Status"), RedeemedDate = r.DateN("RedeemedDate"), Note = r.StrN("Note"),
        }));
        target.PriceOverrides.AddRange(src.Table("PriceOverrides").Select(r => new PriceOverride
        {
            Id = r.Int("OverrideID"), DocType = r.Str("DocType"), DocNumber = r.Str("DocNumber"), ProductName = r.Str("ProductName"),
            StandardPrice = r.Dec("StandardPrice"), OverridePrice = r.Dec("OverridePrice"), ChangedByName = r.Str("ChangedByName"), ChangedAt = Utc(r, "ChangedAt"),
        }));
        await target.SaveChangesAsync();

        // ---- finance ----
        // The ledger links to customers/suppliers by NAME only. Link by id where the name is unambiguous; report the rest.
        var custByName = target.Customers.Local.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var supByName = target.Suppliers.Local.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var r in src.Table("Ledger"))
        {
            var isCustomer = r.Str("AccountType") == LedgerAccountTypes.Customer;
            int? customerId = null, supplierId = null;
            var name = r.Str("AccountName");
            if (isCustomer) { if (custByName.TryGetValue(name, out var l) && l.Count == 1) customerId = l[0].Id; else report.Warn($"Ledger {r.Int("LedgerID")}: customer \"{name}\" {(custByName.ContainsKey(name) ? "is ambiguous" : "no longer exists")} — left unlinked."); }
            else { if (supByName.TryGetValue(name, out var l) && l.Count == 1) supplierId = l[0].Id; else report.Warn($"Ledger {r.Int("LedgerID")}: supplier \"{name}\" {(supByName.ContainsKey(name) ? "is ambiguous" : "no longer exists")} — left unlinked."); }
            target.Ledger.Add(new LedgerEntry
            {
                Id = r.Int("LedgerID"), EntryDate = r.Date("EntryDate"), AccountType = r.Str("AccountType"), AccountName = name, CustomerId = customerId,
                SupplierId = supplierId, EntryType = r.Str("EntryType"), Amount = r.Dec("Amount"), Reference = r.StrN("Reference"),
            });
        }
        target.Expenses.AddRange(src.Table("Expenses").Select(r => new Expense
        {
            Id = r.Int("ExpenseID"), Category = r.Str("Category"), ExpenseDate = r.Date("ExpenseDate"), Amount = r.Dec("Amount"), Note = r.StrN("Note"),
            CreatedByUserId = MapUser(r.Int("CreatedByUserID"), $"Expense {r.Int("ExpenseID")}"),
        }));

        // ---- stock history ----
        target.StockMovements.AddRange(src.Table("StockMovements").Select(r => new StockMovement
        {
            Id = r.Int("MovementID"), ProductId = r.Int("ProductID"), WarehouseId = r.Int("WarehouseID"), MovementType = r.Str("MovementType"),
            Quantity = r.Int("Quantity"), ReferenceType = r.StrN("ReferenceType"), ReferenceId = r.IntN("ReferenceID"), MovementDate = Utc(r, "MovementDate"),
            UserId = MapUser(r.Int("UserID"), $"StockMovement {r.Int("MovementID")}"),
        }));
        await target.SaveChangesAsync();

        if (report.Failures.Count > 0) throw new InvalidOperationException("Import aborted: " + report.Failures.Count + " failure(s) — see the report.");
        await SynthesizeOpeningBalancesAsync();
    }

    /// <summary>
    /// Stock that pre-dates movement logging (opening quantities typed with a product, "PO-RECEIPT" receipts) has batches but no
    /// history. Adds ONE movement per (product, warehouse) so that Σ(IN) − Σ(OUT) ± ADJUST equals the quantity actually on the
    /// shelf. It invents nothing beyond what the current batch quantities already imply, and every such row is flagged.
    /// </summary>
    private async Task SynthesizeOpeningBalancesAsync()
    {
        var onHand = target.StockBatches.Local.GroupBy(b => (b.ProductId, b.WarehouseId)).ToDictionary(g => g.Key, g => g.Sum(b => b.QuantityOnHand));
        var logged = target.StockMovements.Local.GroupBy(m => (m.ProductId, m.WarehouseId))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.MovementType == MovementTypes.Out ? -m.Quantity : m.Quantity));
        var userId = userIdByUsername.Values.Min();
        var created = 0;
        foreach (var key in onHand.Keys.Union(logged.Keys))
        {
            var diff = onHand.GetValueOrDefault(key) - logged.GetValueOrDefault(key);
            if (diff == 0) continue;
            target.StockMovements.Add(new StockMovement
            {
                ProductId = key.ProductId, WarehouseId = key.WarehouseId, MovementType = diff > 0 ? MovementTypes.In : MovementTypes.Adjust,
                Quantity = diff, ReferenceType = MovementReferences.OpeningBalance, MovementDate = DateTime.UtcNow, UserId = userId,
                Note = "migration: reconciles history to stock on hand",
            });
            created++;
        }
        await target.SaveChangesAsync();
        report.Line($"Synthesized {created} opening-balance movement(s) so stock history reconciles to stock on hand.");
    }

    // ------------------------------------------------------------------ verification ------------------------------------------------------------------

    public async Task VerifyAsync()
    {
        var sampleInvoices = options.ExcludeSample ? "AND IsSample = 0" : "";
        report.H("Row counts");
        report.Line("| Table | Source | Target | |"); report.Line("|---|---:|---:|---|");
        async Task Count(string label, string sourceSql, Func<Task<int>> targetCount) => report.Check(label, src.Scalar(sourceSql), await targetCount());
        await Count("categories", "SELECT COUNT(*) AS v FROM Categories", () => target.Categories.CountAsync());
        await Count("warehouses", "SELECT COUNT(*) AS v FROM Warehouses", () => target.Warehouses.CountAsync());
        await Count("products", "SELECT COUNT(*) AS v FROM Products", () => target.Products.CountAsync());
        await Count("stock_batches", "SELECT COUNT(*) AS v FROM StockBatches", () => target.StockBatches.CountAsync());
        await Count("suppliers", "SELECT COUNT(*) AS v FROM Suppliers", () => target.Suppliers.CountAsync());
        await Count("customers", "SELECT COUNT(*) AS v FROM Customers", () => target.Customers.CountAsync());
        await Count("purchase_orders", $"SELECT COUNT(*) AS v FROM PurchaseOrders WHERE 1=1 {sampleInvoices}", () => target.PurchaseOrders.CountAsync());
        await Count("invoices", $"SELECT COUNT(*) AS v FROM Invoices WHERE 1=1 {sampleInvoices}", () => target.Invoices.CountAsync());
        await Count("payments", $"SELECT COUNT(*) AS v FROM Payments p JOIN Invoices i ON i.InvoiceID = p.InvoiceID WHERE 1=1 {sampleInvoices.Replace("IsSample", "i.IsSample")}", () => target.Payments.CountAsync());
        await Count("rebate_entries", "SELECT COUNT(*) AS v FROM RebateEntries", () => target.RebateEntries.CountAsync());
        await Count("ledger", "SELECT COUNT(*) AS v FROM Ledger", () => target.Ledger.CountAsync());
        await Count("expenses", "SELECT COUNT(*) AS v FROM Expenses", () => target.Expenses.CountAsync());
        await Count("price_overrides", "SELECT COUNT(*) AS v FROM PriceOverrides", () => target.PriceOverrides.CountAsync());

        report.H("Control totals (must match to the cent)");
        report.Line("| Measure | Source | Target | |"); report.Line("|---|---:|---:|---|");
        report.Check("Σ invoices.TotalAmount", src.Scalar($"SELECT ISNULL(SUM(TotalAmount),0) AS v FROM Invoices WHERE 1=1 {sampleInvoices}"), await target.Invoices.SumAsync(i => (decimal?)i.TotalAmount) ?? 0);
        report.Check("Σ invoices.AmountPaid", src.Scalar($"SELECT ISNULL(SUM(AmountPaid),0) AS v FROM Invoices WHERE 1=1 {sampleInvoices}"), await target.Invoices.SumAsync(i => (decimal?)i.AmountPaid) ?? 0);
        report.Check("Σ invoices.VATAmount", src.Scalar($"SELECT ISNULL(SUM(VATAmount),0) AS v FROM Invoices WHERE 1=1 {sampleInvoices}"), await target.Invoices.SumAsync(i => (decimal?)i.VatAmount) ?? 0);
        report.Check("Σ invoice_items.LineTotal", src.Scalar($"SELECT ISNULL(SUM(ii.LineTotal),0) AS v FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID=ii.InvoiceID WHERE 1=1 {sampleInvoices.Replace("IsSample", "i.IsSample")}"), await target.InvoiceItems.SumAsync(i => (decimal?)i.LineTotal) ?? 0);
        report.Check("Σ invoice_items qty×UnitCost (COGS)", src.Scalar($"SELECT ISNULL(SUM(ii.Quantity*ii.UnitCost),0) AS v FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID=ii.InvoiceID WHERE 1=1 {sampleInvoices.Replace("IsSample", "i.IsSample")}"), await target.InvoiceItems.SumAsync(i => (decimal?)(i.Quantity * i.UnitCost)) ?? 0);
        report.Check("Σ payments.Amount", src.Scalar($"SELECT ISNULL(SUM(p.Amount),0) AS v FROM Payments p JOIN Invoices i ON i.InvoiceID=p.InvoiceID WHERE 1=1 {sampleInvoices.Replace("IsSample", "i.IsSample")}"), await target.Payments.SumAsync(p => (decimal?)p.Amount) ?? 0);
        report.Check("Σ purchase_orders.TotalAmount", src.Scalar($"SELECT ISNULL(SUM(TotalAmount),0) AS v FROM PurchaseOrders WHERE 1=1 {sampleInvoices}"), await target.PurchaseOrders.SumAsync(p => (decimal?)p.TotalAmount) ?? 0);
        report.Check("Σ purchase_orders.AmountPaid", src.Scalar($"SELECT ISNULL(SUM(AmountPaid),0) AS v FROM PurchaseOrders WHERE 1=1 {sampleInvoices}"), await target.PurchaseOrders.SumAsync(p => (decimal?)p.AmountPaid) ?? 0);
        report.Check("Σ customers.Balance (AR)", src.Scalar("SELECT ISNULL(SUM(Balance),0) AS v FROM Customers"), await target.Customers.SumAsync(c => (decimal?)c.Balance) ?? 0);
        report.Check("Σ suppliers.Balance (AP)", src.Scalar("SELECT ISNULL(SUM(Balance),0) AS v FROM Suppliers"), await target.Suppliers.SumAsync(s => (decimal?)s.Balance) ?? 0);
        report.Check("Σ ledger Debit", src.Scalar("SELECT ISNULL(SUM(Amount),0) AS v FROM Ledger WHERE EntryType='Debit'"), await target.Ledger.Where(l => l.EntryType == "Debit").SumAsync(l => (decimal?)l.Amount) ?? 0);
        report.Check("Σ ledger Credit", src.Scalar("SELECT ISNULL(SUM(Amount),0) AS v FROM Ledger WHERE EntryType='Credit'"), await target.Ledger.Where(l => l.EntryType == "Credit").SumAsync(l => (decimal?)l.Amount) ?? 0);
        report.Check("Σ expenses.Amount", src.Scalar("SELECT ISNULL(SUM(Amount),0) AS v FROM Expenses"), await target.Expenses.SumAsync(e => (decimal?)e.Amount) ?? 0);
        report.Check("Σ rebates Accrued", src.Scalar("SELECT ISNULL(SUM(Amount),0) AS v FROM RebateEntries WHERE Status='Accrued'"), await target.RebateEntries.Where(r => r.Status == "Accrued").SumAsync(r => (decimal?)r.Amount) ?? 0);
        report.Check("Σ stock on hand", src.Scalar("SELECT ISNULL(SUM(QuantityOnHand),0) AS v FROM StockBatches"), await target.StockBatches.SumAsync(b => (decimal?)b.QuantityOnHand) ?? 0);
        report.Check("Stock value (Σ qty × cost)", src.Scalar("SELECT ISNULL(SUM(sb.QuantityOnHand*p.CostPrice),0) AS v FROM StockBatches sb JOIN Products p ON p.ProductID=sb.ProductID"),
            (await (from b in target.StockBatches join p in target.Products on b.ProductId equals p.Id select (decimal?)(b.QuantityOnHand * p.CostPrice)).SumAsync()) ?? 0);

        report.H("Referential integrity (target)");
        async Task Orphans(string what, Task<int> count) { var n = await count; if (n == 0) report.Line($"- ✓ {what}"); else report.Fail($"{what}: {n} orphan row(s)"); }
        await Orphans("invoice_items → invoices", target.InvoiceItems.CountAsync(i => !target.Invoices.Any(x => x.Id == i.InvoiceId)));
        await Orphans("invoice_items → products", target.InvoiceItems.CountAsync(i => !target.Products.Any(x => x.Id == i.ProductId)));
        await Orphans("payments → invoices", target.Payments.CountAsync(p => !target.Invoices.Any(x => x.Id == p.InvoiceId)));
        await Orphans("purchase_order_items → purchase_orders", target.PurchaseOrderItems.CountAsync(i => !target.PurchaseOrders.Any(x => x.Id == i.PurchaseOrderId)));
        await Orphans("stock_movements → products", target.StockMovements.CountAsync(m => !target.Products.Any(x => x.Id == m.ProductId)));

        report.H("Business-rule consistency (target)");
        var arDrift = await CustomerBalanceDriftAsync();
        if (arDrift.Count == 0) report.Line("- ✓ every Customers.Balance equals what its unpaid invoices still owe");
        else foreach (var d in arDrift) report.Warn($"Customer {d}");
        var apDrift = await SupplierBalanceDriftAsync();
        if (apDrift.Count == 0) report.Line("- ✓ every Suppliers.Balance equals what its unpaid purchase orders still owe");
        else foreach (var d in apDrift) report.Warn($"Supplier {d}");
        var bad = await target.Invoices.Where(i => i.Subtotal - i.DiscountAmount + i.VatAmount != i.TotalAmount).Select(i => i.InvoiceNumber).ToListAsync();
        if (bad.Count == 0) report.Line("- ✓ every invoice: Subtotal − Discount + VAT = Total"); else foreach (var n in bad) report.Warn($"Invoice {n}: Subtotal − Discount + VAT ≠ Total");
        var samples = await target.Invoices.CountAsync(i => i.IsSample);
        if (samples > 0) report.Warn($"{samples} invoice(s) flagged IsSample (demo data) were migrated. Re-run with --exclude-sample to leave them out.");
    }

    private async Task<List<string>> CustomerBalanceDriftAsync()
    {
        var owed = await target.Invoices.Where(i => i.Status != "Paid").GroupBy(i => i.CustomerId).Select(g => new { g.Key, O = g.Sum(i => i.TotalAmount - i.AmountPaid) }).ToDictionaryAsync(x => x.Key, x => x.O);
        return (await target.Customers.ToListAsync()).Where(c => c.Balance != owed.GetValueOrDefault(c.Id))
            .Select(c => $"{c.Id} \"{c.Name}\": Balance {c.Balance:N2} but unpaid invoices owe {owed.GetValueOrDefault(c.Id):N2} (carried over unchanged)").ToList();
    }

    private async Task<List<string>> SupplierBalanceDriftAsync()
    {
        var owed = await target.PurchaseOrders.Where(p => p.PaymentStatus != "Paid").GroupBy(p => p.SupplierId).Select(g => new { g.Key, O = g.Sum(p => p.TotalAmount - p.AmountPaid) }).ToDictionaryAsync(x => x.Key, x => x.O);
        return (await target.Suppliers.ToListAsync()).Where(s => s.Balance != owed.GetValueOrDefault(s.Id))
            .Select(s => $"{s.Id} \"{s.Name}\": Balance {s.Balance:N2} but unpaid orders owe {owed.GetValueOrDefault(s.Id):N2} (carried over unchanged)").ToList();
    }

    /// <summary>Tables that exist in the source but are NOT yet part of the target model (later releases). Reported, never silently dropped.</summary>
    public void ReportSkippedTables()
    {
        report.H("Source tables not migrated yet (later releases — data is untouched in SQL Server)");
        foreach (var t in new[] { "Quotations", "QuotationItems", "Waybills", "ProductSerials", "Employees", "EmployeeMonthly", "EmployeeLoans", "LoanRepayments", "AttendanceEvents", "PriceChanges" })
            if (src.TableExists(t)) report.Line($"- {t}: {src.Scalar($"SELECT COUNT(*) AS v FROM [{t}]"):N0} row(s)");
        report.Line("- Deliberately not migrated: AppSettings (theme/language/licence/backup — desktop concerns), legacy Attendance (superseded by AttendanceEvents).");
    }
}

public static class IdentityImporter
{
    /// <summary>Accounts are shared across businesses and live in the home database; import them once, keyed by Username.</summary>
    public static async Task<Dictionary<string, int>> ImportUsersAsync(SourceReader home, IdentityStore store, Report report)
    {
        var roles = await store.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in home.Query("SELECT u.*, ro.RoleName FROM Users u JOIN Roles ro ON ro.RoleID = u.RoleID"))
        {
            var username = r.Str("Username");
            var existing = await store.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == username.ToUpperInvariant());
            if (existing is not null) { map[username] = existing.Id; report.Line($"- user {username}: already present (skipped)"); continue; }

            var roleName = r.Str("RoleName") == RoleNames.LegacyAdmin ? RoleNames.Admin : RoleNames.Clerk;
            var lockedUntil = r.DateTimeRawN("LockedUntil");
            var hash = r.Str("PasswordHash");
            var user = new AppUser
            {
                UserName = username, NormalizedUserName = username.ToUpperInvariant(), FullName = r.Str("FullName"), PasswordHash = hash,
                SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString(), IsActive = r.Bool("IsActive"),
                // Placeholder hashes never verify (see LegacyAwarePasswordHasher); an admin must issue a first password.
                MustChangePassword = r.Bool("MustChangePassword") || !hash.StartsWith("pbkdf2$"),
                LastLoginAt = r.DateTimeRawN("LastLoginAt") is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null,   // stored UTC in the source
                CreatedAt = DateTime.SpecifyKind(r.DateTimeRaw("CreatedAt") - TimeSpan.FromHours(1), DateTimeKind.Utc),
                AccessFailedCount = r.Int("FailedAttempts"), LockoutEnabled = true,
                LockoutEnd = lockedUntil is { } lu ? new DateTimeOffset(DateTime.SpecifyKind(lu, DateTimeKind.Utc)) : null,   // stored UTC in the source
                CompanyAccess = "chewypets,candid",
            };
            store.Users.Add(user);
            await store.SaveChangesAsync();
            store.UserRoles.Add(new IdentityUserRole<int> { UserId = user.Id, RoleId = roles[roleName] });
            await store.SaveChangesAsync();
            map[username] = user.Id;
            if (!hash.StartsWith("pbkdf2$")) report.Warn($"User {username} has a placeholder password hash — an admin must set a first password before they can sign in.");
        }
        return map;
    }

    public static async Task ImportLoginAuditAsync(SourceReader home, IdentityStore store, Report report)
    {
        if (await store.LoginAudit.AnyAsync()) { report.Line("- login audit: already present (skipped)"); return; }
        foreach (var r in home.Table("LoginAudit"))
            store.LoginAudit.Add(new LoginAuditEntry
            {
                Username = r.Str("Username"), Succeeded = r.Bool("Succeeded"), Reason = r.StrN("Reason"),
                ClientAddress = r.StrN("MachineName") is { } m ? "desktop:" + m : null, AtUtc = DateTime.SpecifyKind(r.DateTimeRaw("AtUtc"), DateTimeKind.Utc),
            });
        await store.SaveChangesAsync();
    }
}
