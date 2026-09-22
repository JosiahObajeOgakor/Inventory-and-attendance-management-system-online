using ClosedXML.Excel;
using Inventory.Application.Abstractions;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Spreadsheet exports (ClosedXML). The desktop app wrote CSV; these are real .xlsx files with typed number and date cells, so Excel
/// sorts and sums them without the "numbers stored as text" nag. Every sheet freezes its header row and auto-fits its columns.
/// </summary>
public sealed class ExcelExporter(IBusinessDbContext db, ICompanyContext company, IClock clock)
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Money = "#,##0.00";

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>A cell that starts with = + - @ would run as a formula when the file is opened, so text from the database is neutralised.</summary>
    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "" : s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;

    private static IXLWorksheet Sheet(XLWorkbook wb, string name, params string[] headers)
    {
        var ws = wb.Worksheets.Add(name);
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        var head = ws.Range(1, 1, 1, Math.Max(1, headers.Length));
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = XLColor.FromHtml("#1f3d34");
        head.Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        return ws;
    }

    private static void Finish(IXLWorksheet ws) { ws.Columns().AdjustToContents(8, 60); }

    // ---------------------------------------------------------------- sheets
    private async Task InventorySheet(XLWorkbook wb, bool withCost, CancellationToken ct)
    {
        var headers = withCost ? new[] { "SKU", "Product", "Category", "Unit", "In stock", "Reorder level", "Cost", "Distributor", "Wholesaler", "Retail", "Stock value at cost", "Barcode" }
                               : new[] { "SKU", "Product", "Category", "Unit", "In stock", "Reorder level", "Distributor", "Wholesaler", "Retail", "Barcode" };
        var ws = Sheet(wb, "Inventory", headers);
        var qty = await db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var rows = await (from p in db.Products.AsNoTracking().Where(x => x.IsActive) join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id orderby c.Name, p.Name select new { p, Cat = c.Name }).ToListAsync(ct);
        var r = 2;
        foreach (var x in rows)
        {
            var q = qty.GetValueOrDefault(x.p.Id);
            var c = 1;
            ws.Cell(r, c++).Value = Safe(x.p.Sku); ws.Cell(r, c++).Value = Safe(x.p.Name); ws.Cell(r, c++).Value = Safe(x.Cat); ws.Cell(r, c++).Value = Safe(x.p.Unit);
            ws.Cell(r, c++).Value = q; ws.Cell(r, c++).Value = x.p.ReorderLevel;
            if (withCost) ws.Cell(r, c++).Value = x.p.CostPrice;
            ws.Cell(r, c++).Value = x.p.PriceDistributor; ws.Cell(r, c++).Value = x.p.PriceWholesaler; ws.Cell(r, c++).Value = x.p.PriceRetail;
            if (withCost) ws.Cell(r, c++).Value = q * x.p.CostPrice;
            ws.Cell(r, c).Value = Safe(x.p.Barcode);
            r++;
        }
        var moneyCols = withCost ? new[] { 7, 8, 9, 10, 11 } : new[] { 7, 8, 9 };
        foreach (var mc in moneyCols) ws.Column(mc).Style.NumberFormat.Format = Money;
        Finish(ws);
    }

    private async Task InvoicesSheet(XLWorkbook wb, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ws = Sheet(wb, "Invoices", "Invoice", "Date", "Customer", "Payment", "Status", "Subtotal", "Discount", "VAT", "Total", "Paid", "Balance", "Due date");
        var rows = await (from i in db.Invoices.AsNoTracking().Where(x => x.InvoiceDate >= @from && x.InvoiceDate <= to) join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id
                          orderby i.InvoiceDate descending, i.Id descending select new { i, c.Name }).ToListAsync(ct);
        var r = 2;
        foreach (var x in rows)
        {
            var i = x.i;
            ws.Cell(r, 1).Value = Safe(i.InvoiceNumber); ws.Cell(r, 2).Value = i.InvoiceDate.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 3).Value = Safe(x.Name);
            ws.Cell(r, 4).Value = Safe(i.PaymentMethod); ws.Cell(r, 5).Value = i.Status;
            ws.Cell(r, 6).Value = i.Subtotal; ws.Cell(r, 7).Value = i.DiscountAmount; ws.Cell(r, 8).Value = i.VatAmount; ws.Cell(r, 9).Value = i.TotalAmount;
            ws.Cell(r, 10).Value = i.AmountPaid; ws.Cell(r, 11).Value = i.Status == PaymentStatuses.Voided ? 0 : i.TotalAmount - i.AmountPaid;
            if (i.DueDate is { } d) ws.Cell(r, 12).Value = d.ToDateTime(TimeOnly.MinValue);
            r++;
        }
        ws.Column(2).Style.DateFormat.Format = "dd MMM yyyy"; ws.Column(12).Style.DateFormat.Format = "dd MMM yyyy";
        foreach (var mc in new[] { 6, 7, 8, 9, 10, 11 }) ws.Column(mc).Style.NumberFormat.Format = Money;
        Finish(ws);
    }

    private async Task CustomersSheet(XLWorkbook wb, CancellationToken ct)
    {
        var ws = Sheet(wb, "Customers", "Customer", "Type", "Contact", "Phone", "Email", "Location", "Address", "Tax ID", "Rebate %", "Credit limit", "Owes us");
        var r = 2;
        foreach (var c in await db.Customers.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
        {
            ws.Cell(r, 1).Value = Safe(c.Name); ws.Cell(r, 2).Value = c.CustomerType; ws.Cell(r, 3).Value = Safe(c.ContactName); ws.Cell(r, 4).Value = Safe(c.Phone);
            ws.Cell(r, 5).Value = Safe(c.Email); ws.Cell(r, 6).Value = Safe(c.Location); ws.Cell(r, 7).Value = Safe(c.Address); ws.Cell(r, 8).Value = Safe(c.TaxId);
            ws.Cell(r, 9).Value = c.RebateRatePct; ws.Cell(r, 10).Value = c.CreditLimit; ws.Cell(r, 11).Value = c.Balance;
            r++;
        }
        ws.Column(10).Style.NumberFormat.Format = Money; ws.Column(11).Style.NumberFormat.Format = Money;
        Finish(ws);
    }

    private async Task ExpensesSheet(XLWorkbook wb, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ws = Sheet(wb, "Expenses", "Date", "Category", "Amount", "Note");
        var r = 2;
        foreach (var e in await db.Expenses.AsNoTracking().Where(x => x.ExpenseDate >= from && x.ExpenseDate <= to).OrderByDescending(x => x.ExpenseDate).ThenByDescending(x => x.Id).ToListAsync(ct))
        {
            ws.Cell(r, 1).Value = e.ExpenseDate.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 2).Value = e.Category; ws.Cell(r, 3).Value = e.Amount; ws.Cell(r, 4).Value = Safe(e.Note);
            r++;
        }
        ws.Column(1).Style.DateFormat.Format = "dd MMM yyyy"; ws.Column(3).Style.NumberFormat.Format = Money;
        if (r > 2) { ws.Cell(r, 2).Value = "Total"; ws.Cell(r, 3).FormulaA1 = $"SUM(C2:C{r - 1})"; ws.Range(r, 2, r, 3).Style.Font.Bold = true; ws.Cell(r, 3).Style.NumberFormat.Format = Money; }
        Finish(ws);
    }

    private async Task LedgerSheet(XLWorkbook wb, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ws = Sheet(wb, "Ledger", "Date", "Account type", "Account", "Debit", "Credit", "Reference");
        var r = 2;
        foreach (var l in await db.Ledger.AsNoTracking().Where(x => x.EntryDate >= from && x.EntryDate <= to).OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.Id).ToListAsync(ct))
        {
            ws.Cell(r, 1).Value = l.EntryDate.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 2).Value = l.AccountType; ws.Cell(r, 3).Value = Safe(l.AccountName);
            if (l.EntryType == LedgerEntryTypes.Debit) ws.Cell(r, 4).Value = l.Amount; else ws.Cell(r, 5).Value = l.Amount;
            ws.Cell(r, 6).Value = Safe(l.Reference);
            r++;
        }
        ws.Column(1).Style.DateFormat.Format = "dd MMM yyyy"; ws.Column(4).Style.NumberFormat.Format = Money; ws.Column(5).Style.NumberFormat.Format = Money;
        Finish(ws);
    }

    /// <summary>Revenue, cost of goods, expenses and profit for the period, then who bought and what sold.</summary>
    private async Task ReportSheets(XLWorkbook wb, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var invs = await db.Invoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to && i.Status != PaymentStatuses.Voided && !i.IsSample).Select(i => new { i.Id, i.CustomerId, i.Subtotal, i.DiscountAmount, i.VatAmount, i.TotalAmount, i.AmountPaid }).ToListAsync(ct);
        var ids = invs.Select(i => i.Id).ToList();
        var items = await db.InvoiceItems.AsNoTracking().Where(x => ids.Contains(x.InvoiceId)).Select(x => new { x.ProductId, x.Quantity, x.LineTotal, x.UnitCost }).ToListAsync(ct);
        var cogs = items.Sum(x => x.Quantity * x.UnitCost);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.ExpenseDate >= from && e.ExpenseDate <= to).SumAsync(e => (decimal?)e.Amount, ct) ?? 0;
        var revenue = invs.Sum(i => i.TotalAmount - i.VatAmount);

        var ws = Sheet(wb, "Summary", "Measure", "Amount");
        void Line(int row, string label, decimal v, bool bold = false) { ws.Cell(row, 1).Value = label; ws.Cell(row, 2).Value = v; ws.Cell(row, 2).Style.NumberFormat.Format = Money; if (bold) ws.Range(row, 1, row, 2).Style.Font.Bold = true; }
        ws.Cell(1, 3).Value = $"{company.LegalName}, {from:dd MMM yyyy} to {to:dd MMM yyyy}";
        Line(2, "Invoices", invs.Count); ws.Cell(2, 2).Style.NumberFormat.Format = "0";
        Line(3, "Sales before VAT", revenue); Line(4, "Discounts given", invs.Sum(i => i.DiscountAmount)); Line(5, "VAT collected", invs.Sum(i => i.VatAmount));
        Line(6, "Cost of goods sold", cogs); Line(7, "Gross profit", revenue - cogs, true); Line(8, "Expenses", expenses); Line(9, "Net profit", revenue - cogs - expenses, true);
        Line(10, "Money received on these invoices", invs.Sum(i => i.AmountPaid));
        Finish(ws);

        var byCust = invs.GroupBy(i => i.CustomerId).Select(g => new { Id = g.Key, N = g.Count(), T = g.Sum(i => i.TotalAmount) }).OrderByDescending(x => x.T).ToList();
        var names = await db.Customers.AsNoTracking().Where(c => byCust.Select(b => b.Id).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var wc = Sheet(wb, "By customer", "Customer", "Invoices", "Total");
        var r = 2; foreach (var b in byCust) { wc.Cell(r, 1).Value = Safe(names.GetValueOrDefault(b.Id)); wc.Cell(r, 2).Value = b.N; wc.Cell(r, 3).Value = b.T; r++; }
        wc.Column(3).Style.NumberFormat.Format = Money; Finish(wc);

        var byProd = items.GroupBy(i => i.ProductId).Select(g => new { Id = g.Key, Q = g.Sum(i => i.Quantity), R = g.Sum(i => i.LineTotal), C = g.Sum(i => i.Quantity * i.UnitCost) }).OrderByDescending(x => x.R).ToList();
        var pn = await db.Products.AsNoTracking().Where(p => byProd.Select(b => b.Id).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var wp = Sheet(wb, "By product", "Product", "Quantity", "Revenue", "Cost", "Margin");
        r = 2; foreach (var b in byProd) { wp.Cell(r, 1).Value = Safe(pn.GetValueOrDefault(b.Id)); wp.Cell(r, 2).Value = b.Q; wp.Cell(r, 3).Value = b.R; wp.Cell(r, 4).Value = b.C; wp.Cell(r, 5).Value = b.R - b.C; r++; }
        foreach (var mc in new[] { 3, 4, 5 }) wp.Column(mc).Style.NumberFormat.Format = Money; Finish(wp);
    }

    // ---------------------------------------------------------------- public
    /// <summary>Stock list. Cost and stock value are only included for admins.</summary>
    public async Task<byte[]> InventoryAsync(bool isAdmin, CancellationToken ct) { using var wb = new XLWorkbook(); await InventorySheet(wb, isAdmin, ct); return Save(wb); }
    public async Task<byte[]> InvoicesAsync(DateOnly from, DateOnly to, CancellationToken ct) { using var wb = new XLWorkbook(); await InvoicesSheet(wb, from, to, ct); return Save(wb); }
    public async Task<byte[]> CustomersAsync(CancellationToken ct) { using var wb = new XLWorkbook(); await CustomersSheet(wb, ct); return Save(wb); }
    public async Task<byte[]> ExpensesAsync(DateOnly from, DateOnly to, CancellationToken ct) { using var wb = new XLWorkbook(); await ExpensesSheet(wb, from, to, ct); return Save(wb); }
    public async Task<byte[]> LedgerAsync(DateOnly from, DateOnly to, CancellationToken ct) { using var wb = new XLWorkbook(); await LedgerSheet(wb, from, to, ct); return Save(wb); }
    public async Task<byte[]> ReportAsync(DateOnly from, DateOnly to, CancellationToken ct) { using var wb = new XLWorkbook(); await ReportSheets(wb, from, to, ct); return Save(wb); }

    /// <summary>Everything in one workbook (Admin): the hand-over / accountant pack.</summary>
    public async Task<byte[]> FullAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var wb = new XLWorkbook();
        await ReportSheets(wb, from, to, ct);
        await InventorySheet(wb, true, ct); await InvoicesSheet(wb, from, to, ct); await CustomersSheet(wb, ct);
        await ExpensesSheet(wb, from, to, ct); await LedgerSheet(wb, from, to, ct);
        return Save(wb);
    }
}
