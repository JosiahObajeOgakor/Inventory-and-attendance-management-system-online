using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Ledger;

/// <summary>
/// Keeps the books in step with the documents the business already records (sales, payments, purchases, expenses, stock write-offs) WITHOUT changing
/// how any of them is saved. Each document becomes its journal exactly once (the ledger refuses a second journal for the same document and event), a
/// voided sale gets its journal reversed, and nothing is ever edited. It runs whenever the accounting screens are opened and on a timer, so history
/// is picked up too: no month-end import and no opening entries per document.
/// </summary>
public sealed class LedgerSync(IBusinessDbContext db, GeneralLedger gl)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly CurrentUser System = new(0, "System", "ADMIN");
    private static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(1));   // Lagos

    private async Task<HashSet<string>> PostedAsync(string sourceType, CancellationToken ct) =>
        (await db.GlJournals.AsNoTracking().Where(j => j.SourceType == sourceType).Select(j => j.SourceId + "|" + j.SourceEvent).ToListAsync(ct)).ToHashSet();

    /// <summary>Posts everything not yet in the books. Returns how many journals were added.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var added = 0;
            added += await SalesAsync(ct);
            added += await PaymentsAsync(ct);
            added += await PurchasesAsync(ct);
            added += await SupplierPaymentsAsync(ct);
            added += await ExpensesAsync(ct);
            added += await StockWriteOffsAsync(ct);
            return added;
        }
        finally { Gate.Release(); }
    }

    private async Task<int> SaveAsync(int pending, CancellationToken ct)
    {
        if (pending == 0) return 0;
        try { await db.SaveChangesAsync(ct); return pending; }
        catch (DbUpdateException) { db.ClearTracker(); return 0; }   // another request posted the same document first: the books already have it
    }

    private async Task<int> SalesAsync(CancellationToken ct)
    {
        var done = await PostedAsync("Invoice", ct);
        var n = 0;
        var todo = await db.Invoices.AsNoTracking().Include(i => i.Items).Where(i => !i.IsSample).OrderBy(i => i.Id).ToListAsync(ct);
        foreach (var i in todo)
        {
            var id = i.Id.ToString();
            try
            {
                if (!done.Contains(id + "|sale"))
                {
                    await gl.ForWarehouseAsync(i.WarehouseId, ct);
                    var revenue = i.TotalAmount - i.VatAmount;
                    if (await gl.PostAsync("Invoice", id, "sale", i.InvoiceDate, $"Sale {i.InvoiceNumber}",
                            [PostLine.Dr(GlCodes.Receivables, i.TotalAmount, customerId: i.CustomerId), PostLine.Cr(GlCodes.SalesRevenue, revenue, customerId: i.CustomerId), PostLine.Cr(GlCodes.VatOutput, i.VatAmount)], System, ct) is not null) n++;
                    var cost = Math.Round(i.Items.Sum(x => x.Quantity * x.UnitCost), 2);
                    if (await gl.PostAsync("Invoice", id, "cogs", i.InvoiceDate, $"Cost of {i.InvoiceNumber}",
                            [PostLine.Dr(GlCodes.CostOfGoodsSold, cost), PostLine.Cr(GlCodes.Inventory, cost)], System, ct) is not null) n++;
                }
                if (i.VoidedAt is DateTime voided && !done.Contains(id + "|void"))
                {
                    var on = Day(voided);
                    var r1 = await gl.ReverseAsync("Invoice", id, "sale", "void", on, $"Void of {i.InvoiceNumber}", System, ct);
                    var r2 = await gl.ReverseAsync("Invoice", id, "cogs", "void-cogs", on, $"Void of {i.InvoiceNumber}", System, ct);
                    if (r1 is not null) n++;
                    if (r2 is not null) n++;
                }
            }
            catch (BusinessRuleException) { /* dated in a closed month: left for a finance controller to reopen or reclass */ }
            finally { gl.Branch = null; }
        }
        return await SaveAsync(n, ct);
    }

    private async Task<int> PaymentsAsync(CancellationToken ct)
    {
        var done = await PostedAsync("Payment", ct);
        var n = 0;
        var rows = await (from p in db.Payments.AsNoTracking() join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                          where !i.IsSample select new { p.Id, p.PaymentDate, p.Amount, p.Method, i.CustomerId, i.InvoiceNumber, i.WarehouseId }).ToListAsync(ct);
        foreach (var p in rows.Where(p => !done.Contains(p.Id + "|received")))
        {
            try
            {
                await gl.ForWarehouseAsync(p.WarehouseId, ct);
                if (await gl.PostAsync("Payment", p.Id.ToString(), "received", Day(p.PaymentDate), $"Payment on {p.InvoiceNumber} ({p.Method})",
                        [PostLine.Dr(Cashbox(p.Method), p.Amount), PostLine.Cr(GlCodes.Receivables, p.Amount, customerId: p.CustomerId)], System, ct) is not null) n++;
            }
            catch (BusinessRuleException) { }
            finally { gl.Branch = null; }
        }
        return await SaveAsync(n, ct);
    }

    private async Task<int> PurchasesAsync(CancellationToken ct)
    {
        var done = await PostedAsync("PurchaseOrder", ct);
        var n = 0;
        var rows = await db.PurchaseOrders.AsNoTracking().Where(p => p.Status == PurchaseStatuses.Received && !p.IsSample).ToListAsync(ct);
        foreach (var p in rows.Where(p => !done.Contains(p.Id + "|received")))
        {
            try
            {
                if (await gl.PostAsync("PurchaseOrder", p.Id.ToString(), "received", p.OrderDate, $"Purchase {p.PoNumber}",
                        [PostLine.Dr(GlCodes.Inventory, p.TotalAmount), PostLine.Cr(GlCodes.Payables, p.TotalAmount, supplierId: p.SupplierId)], System, ct) is not null) n++;
            }
            catch (BusinessRuleException) { }
        }
        return await SaveAsync(n, ct);
    }

    private async Task<int> SupplierPaymentsAsync(CancellationToken ct)
    {
        var done = await PostedAsync("SupplierPayment", ct);
        var n = 0;
        foreach (var p in (await db.SupplierPayments.AsNoTracking().ToListAsync(ct)).Where(p => !done.Contains(p.Id + "|paid")))
        {
            try
            {
                if (await gl.PostAsync("SupplierPayment", p.Id.ToString(), "paid", Day(p.PaymentDate), $"Payment to supplier ({p.Method})",
                        [PostLine.Dr(GlCodes.Payables, p.Amount, supplierId: p.SupplierId), PostLine.Cr(Cashbox(p.Method), p.Amount)], System, ct) is not null) n++;
            }
            catch (BusinessRuleException) { }
        }
        return await SaveAsync(n, ct);
    }

    private async Task<int> ExpensesAsync(CancellationToken ct)
    {
        var done = await PostedAsync("Expense", ct);
        var n = 0;
        foreach (var e in (await db.Expenses.AsNoTracking().ToListAsync(ct)).Where(e => !done.Contains(e.Id + "|spent")))
        {
            try
            {
                if (await gl.PostAsync("Expense", e.Id.ToString(), "spent", e.ExpenseDate, $"{e.Category}{(string.IsNullOrWhiteSpace(e.Note) ? "" : ": " + e.Note)}",
                        [PostLine.Dr(GlCodes.ForExpenseCategory(e.Category), e.Amount), PostLine.Cr(GlCodes.Cash, e.Amount)], System, ct) is not null) n++;
            }
            catch (BusinessRuleException) { }
        }
        return await SaveAsync(n, ct);
    }

    /// <summary>Stock written off or found by a manual adjustment, at today's cost price.</summary>
    private async Task<int> StockWriteOffsAsync(CancellationToken ct)
    {
        var done = await PostedAsync("StockAdjust", ct);
        var n = 0;
        var rows = await (from m in db.StockMovements.AsNoTracking() join p in db.Products.AsNoTracking() on m.ProductId equals p.Id
                          where m.MovementType == MovementTypes.Adjust && m.ReferenceType == MovementReferences.Manual
                          select new { m.Id, m.MovementDate, m.Quantity, m.WarehouseId, m.Note, p.CostPrice, p.Name }).ToListAsync(ct);
        foreach (var m in rows.Where(m => !done.Contains(m.Id + "|adjust")))
        {
            var value = Math.Round(Math.Abs(m.Quantity) * m.CostPrice, 2);
            if (value == 0) continue;
            try
            {
                await gl.ForWarehouseAsync(m.WarehouseId, ct);
                PostLine[] lines = m.Quantity < 0
                    ? [PostLine.Dr(GlCodes.Shrinkage, value), PostLine.Cr(GlCodes.Inventory, value)]
                    : [PostLine.Dr(GlCodes.Inventory, value), PostLine.Cr(GlCodes.InventoryAdjustments, value)];
                if (await gl.PostAsync("StockAdjust", m.Id.ToString(), "adjust", Day(m.MovementDate), $"Stock adjustment {m.Quantity:+#;-#} {m.Name}{(string.IsNullOrWhiteSpace(m.Note) ? "" : ": " + m.Note)}", lines, System, ct) is not null) n++;
            }
            catch (BusinessRuleException) { }
            finally { gl.Branch = null; }
        }
        return await SaveAsync(n, ct);
    }

    private static string Cashbox(string? method) => string.Equals(method, "ALATPay", StringComparison.OrdinalIgnoreCase) ? GlCodes.AlatPayClearing : GlCodes.ForPaymentMethod(method);
}
