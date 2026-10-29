using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Sales;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Ledger;

/// <summary>
/// Keeps the books in step with the documents the business already records (sales, payments, purchases, supplies, expenses, production, stock
/// write-offs) WITHOUT changing how any of them is saved. For every document it works out the journal the document calls for TODAY and compares it
/// with the journal in the books: a new document is posted, an edited one has its old journal reversed and the corrected one posted, and a voided or
/// deleted one is reversed. Nothing posted is ever edited. It runs whenever the accounting screens are opened, so history is picked up too.
/// A document the books cannot take (a closed month, a bad figure) is skipped and logged; it never stops the others or the screens.
/// </summary>
public sealed class LedgerSync(IBusinessDbContext db, GeneralLedger gl, ILogger<LedgerSync> log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly CurrentUser System = new(0, "System", "ADMIN");
    private static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(1));   // Lagos

    /// <summary>
    /// The journal one document calls for now. A negative amount is fine: each account is netted onto its debit or credit side before posting.
    /// <paramref name="Frozen"/>: valued at the cost price of the day it is first posted, and compared by date and memo only, so a later cost
    /// price change does not re-post history.
    /// </summary>
    private sealed record Want(string Id, string Event, DateOnly Date, string Memo, PostLine[] Lines, int? WarehouseId = null, bool Frozen = false);

    /// <summary>Posts and corrects everything out of step with the documents. Returns how many documents were brought into line.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var n = 0;
            n += await KeepAsync("Invoice", await SalesAsync(ct), ct);
            n += await KeepAsync("Payment", await PaymentsAsync(ct), ct);
            n += await KeepAsync("PurchaseOrder", await PurchasesAsync(ct), ct);
            n += await KeepAsync("SupplierPayment", await SupplierPaymentsAsync(ct), ct);
            n += await KeepAsync("Supply", await SuppliesAsync(ct), ct);
            n += await KeepAsync("Expense", await ExpensesAsync(ct), ct);
            n += await KeepAsync("Production", await ProductionAsync(ct), ct);
            n += await KeepAsync("StockAdjust", await StockWriteOffsAsync(ct), ct);
            return n;
        }
        finally { Gate.Release(); }
    }

    // ------------------------------------------------------------------ what each document calls for

    private async Task<List<Want>> SalesAsync(CancellationToken ct)
    {
        var rows = await db.Invoices.AsNoTracking().Include(i => i.Items).Where(i => !i.IsSample && i.VoidedAt == null && i.Status != PaymentStatuses.Voided).ToListAsync(ct);
        var want = new List<Want>();
        foreach (var i in rows)
        {
            var id = i.Id.ToString();
            want.Add(new(id, "sale", i.InvoiceDate, $"Sale {i.InvoiceNumber}",
                [PostLine.Dr(GlCodes.Receivables, i.TotalAmount, customerId: i.CustomerId), PostLine.Cr(GlCodes.SalesRevenue, i.TotalAmount - i.VatAmount, customerId: i.CustomerId), PostLine.Cr(GlCodes.VatOutput, i.VatAmount)],
                i.WarehouseId));
            var cost = i.Items.Sum(x => x.Quantity * x.UnitCost);
            want.Add(new(id, "cogs", i.InvoiceDate, $"Cost of {i.InvoiceNumber}", [PostLine.Dr(GlCodes.CostOfGoodsSold, cost), PostLine.Cr(GlCodes.Inventory, cost)], i.WarehouseId));
        }
        return want;
    }

    private async Task<List<Want>> PaymentsAsync(CancellationToken ct)
    {
        var rows = await (from p in db.Payments.AsNoTracking() join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                          where !i.IsSample select new { p.Id, p.PaymentDate, p.Amount, p.Method, i.CustomerId, i.InvoiceNumber, i.WarehouseId }).ToListAsync(ct);
        // Account credit is not money coming in: it moves what a customer has already paid between their own sales (a negative row takes it off
        // an edited sale, a positive one settles another), so it goes through customer credits, never the till or the bank.
        return rows.Select(p =>
        {
            var credit = p.Method == InvoiceEditService.AccountCredit;
            return new Want(p.Id.ToString(), "received", Day(p.PaymentDate),
                credit ? $"Customer credit {(p.Amount < 0 ? "moved off" : "applied to")} {p.InvoiceNumber}" : $"Payment on {p.InvoiceNumber} ({p.Method})",
                [credit ? PostLine.Dr(GlCodes.CustomerDeposits, p.Amount, customerId: p.CustomerId) : PostLine.Dr(Cashbox(p.Method), p.Amount), PostLine.Cr(GlCodes.Receivables, p.Amount, customerId: p.CustomerId)],
                p.WarehouseId);
        }).ToList();
    }

    private async Task<List<Want>> PurchasesAsync(CancellationToken ct) =>
        (await db.PurchaseOrders.AsNoTracking().Where(p => p.Status == PurchaseStatuses.Received && !p.IsSample).ToListAsync(ct))
        .Select(p => new Want(p.Id.ToString(), "received", p.OrderDate, $"Purchase {p.PoNumber}",
            [PostLine.Dr(GlCodes.Inventory, p.TotalAmount), PostLine.Cr(GlCodes.Payables, p.TotalAmount, supplierId: p.SupplierId)])).ToList();

    private async Task<List<Want>> SupplierPaymentsAsync(CancellationToken ct) =>
        (await db.SupplierPayments.AsNoTracking().ToListAsync(ct))
        .Select(p => new Want(p.Id.ToString(), "paid", Day(p.PaidAt), $"Payment to supplier ({p.Method})",
            [PostLine.Dr(GlCodes.Payables, p.Amount, supplierId: p.SupplierId), PostLine.Cr(Cashbox(p.Method), p.Amount)])).ToList();

    /// <summary>
    /// Supply records (goods or materials bought, and what has been paid on them). Goods added to stock go to inventory; materials that are not
    /// stock items (feed inputs for production, say) are production costs. What has been paid is one journal that follows the paid total.
    /// </summary>
    private async Task<List<Want>> SuppliesAsync(CancellationToken ct)
    {
        var want = new List<Want>();
        foreach (var s in await db.Supplies.AsNoTracking().ToListAsync(ct))
        {
            var id = s.Id.ToString();
            want.Add(new(id, "received", s.SupplyDate, $"Supply {s.Reference}",
                [PostLine.Dr(s.AddedToStock ? GlCodes.Inventory : GlCodes.ProductionInputs, s.TotalAmount), PostLine.Cr(GlCodes.Payables, s.TotalAmount, supplierId: s.SupplierId)], s.WarehouseId));
            want.Add(new(id, "paid", s.SupplyDate, $"Paid on supply {s.Reference}{(string.IsNullOrWhiteSpace(s.PaymentMethod) ? "" : $" ({s.PaymentMethod})")}",
                [PostLine.Dr(GlCodes.Payables, s.AmountPaid, supplierId: s.SupplierId), PostLine.Cr(Cashbox(s.PaymentMethod), s.AmountPaid)], s.WarehouseId));
        }
        return want;
    }

    private async Task<List<Want>> ExpensesAsync(CancellationToken ct) =>
        (await db.Expenses.AsNoTracking().ToListAsync(ct))
        .Select(e => new Want(e.Id.ToString(), "spent", e.ExpenseDate, $"{e.Category}{(string.IsNullOrWhiteSpace(e.Note) ? "" : ": " + e.Note)}",
            [PostLine.Dr(GlCodes.ForExpenseCategory(e.Category), e.Amount), PostLine.Cr(GlCodes.Cash, e.Amount)])).ToList();

    /// <summary>Goods made: they join inventory at the product's cost price, and that cost comes off the production costs (materials are expensed as they are bought).</summary>
    private async Task<List<Want>> ProductionAsync(CancellationToken ct)
    {
        var rows = await (from m in db.StockMovements.AsNoTracking() join p in db.Products.AsNoTracking() on m.ProductId equals p.Id
                          where m.ReferenceType == MovementReferences.Production && m.MovementType == MovementTypes.In && m.Quantity > 0
                          select new { m.Id, m.MovementDate, m.Quantity, m.WarehouseId, p.CostPrice, p.Name }).ToListAsync(ct);
        return rows.Select(m =>
        {
            var value = m.Quantity * m.CostPrice;
            return new Want(m.Id.ToString(), "made", Day(m.MovementDate), $"Production {m.Quantity:N0} × {m.Name}",
                [PostLine.Dr(GlCodes.Inventory, value), PostLine.Cr(GlCodes.ProductionAbsorbed, value)], m.WarehouseId, Frozen: true);
        }).ToList();
    }

    /// <summary>Stock written off or found by a manual adjustment, at the cost price of the day it is first posted.</summary>
    private async Task<List<Want>> StockWriteOffsAsync(CancellationToken ct)
    {
        var rows = await (from m in db.StockMovements.AsNoTracking() join p in db.Products.AsNoTracking() on m.ProductId equals p.Id
                          where m.MovementType == MovementTypes.Adjust && m.ReferenceType == MovementReferences.Manual
                          select new { m.Id, m.MovementDate, m.Quantity, m.WarehouseId, m.Note, p.CostPrice, p.Name }).ToListAsync(ct);
        return rows.Select(m =>
        {
            var value = Math.Abs(m.Quantity) * m.CostPrice;
            PostLine[] lines = m.Quantity < 0
                ? [PostLine.Dr(GlCodes.Shrinkage, value), PostLine.Cr(GlCodes.Inventory, value)]
                : [PostLine.Dr(GlCodes.Inventory, value), PostLine.Cr(GlCodes.InventoryAdjustments, value)];
            return new Want(m.Id.ToString(), "adjust", Day(m.MovementDate), $"Stock adjustment {m.Quantity:+#;-#} {m.Name}{(string.IsNullOrWhiteSpace(m.Note) ? "" : ": " + m.Note)}", lines, m.WarehouseId, Frozen: true);
        }).ToList();
    }

    // ------------------------------------------------------------------ bringing the books into line

    /// <summary>
    /// Compares each document's journal in the books (the latest posted, non-reversal journal for that document and event) with what it calls for
    /// now, and posts, corrects or reverses. Each document is saved on its own, so one the books refuse never holds up another.
    /// </summary>
    private async Task<int> KeepAsync(string sourceType, List<Want> wanted, CancellationToken ct)
    {
        var accounts = await gl.AccountsAsync(ct);
        var codeOf = accounts.ToDictionary(kv => kv.Value, kv => kv.Key);
        var journals = await db.GlJournals.AsNoTracking().Include(j => j.Lines).Where(j => j.SourceType == sourceType).ToListAsync(ct);
        var used = journals.Select(j => (j.SourceId, j.SourceEvent)).ToHashSet();
        var live = journals.Where(j => j.Status == GlJournalStatuses.Posted && j.ReversalOfId == null)
            .GroupBy(j => (j.SourceId, Base(j.SourceEvent))).ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.Id).First());
        var want = wanted.Select(w => w with { Lines = Normal(w.Lines, accounts) }).Where(w => w.Lines.Length > 0)
            .GroupBy(w => (w.Id, w.Event)).ToDictionary(g => g.Key, g => g.First());
        // A line already ticked off against a bank statement stays as it is: reversing it would undo a reconciliation someone signed.
        var liveLineIds = live.Values.SelectMany(j => j.Lines.Select(l => l.Id)).ToList();
        var reconciled = (await db.BankStatementLines.AsNoTracking().Where(s => s.GlLineId != null && liveLineIds.Contains(s.GlLineId.Value))
            .Select(s => s.GlLineId!.Value).ToListAsync(ct)).ToHashSet();

        var n = 0;
        foreach (var key in live.Keys.Union(want.Keys).OrderBy(k => k.Item1.Length).ThenBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            live.TryGetValue(key, out var j); want.TryGetValue(key, out var w);
            if (j is not null && w is not null && Same(j, w, codeOf)) continue;
            if (j is not null && j.Lines.Any(l => reconciled.Contains(l.Id))) continue;
            try
            {
                await gl.ForWarehouseAsync(w?.WarehouseId, ct);
                if (j is not null) await ReverseAsync(sourceType, j, w is null ? $"Reversal of {j.Memo} (removed)" : $"Reversal of {j.Memo} (changed)", ct);
                if (w is not null)
                    await gl.PostAsync(sourceType, w.Id, used.Contains((w.Id, w.Event)) ? GeneralLedger.Once(w.Event) : w.Event, w.Date, w.Memo, w.Lines, System, ct);
                await db.SaveChangesAsync(ct);
                n++;
            }
            catch (BusinessRuleException) { db.ClearTracker(); }   // dated in a closed month: left for a finance controller to reopen or reclass
            catch (DbUpdateException) { db.ClearTracker(); }       // another request posted the same document first: the books already have it
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ClearTracker();
                log.LogWarning(ex, "Ledger: {Type} {Id} {Event} could not be posted and was skipped", sourceType, key.Item1, key.Item2);
            }
            finally { gl.Branch = null; }
        }
        return n;
    }

    /// <summary>Reverses on the original date, so the month it belonged to is corrected; on today's date when that month is closed.</summary>
    private async Task ReverseAsync(string sourceType, GlJournal j, string memo, CancellationToken ct)
    {
        var on = j.Date;
        try { await gl.EnsureOpenAsync(on, false, ct); }
        catch (BusinessRuleException) { on = Day(DateTime.UtcNow); }
        await gl.ReverseAsync(sourceType, j.SourceId, j.SourceEvent, GeneralLedger.Once("rev"), on, memo, System, ct);
    }

    /// <summary>"sale", or a later version of it "sale:&lt;id&gt;".</summary>
    private static string Base(string sourceEvent) => sourceEvent.Split(':')[0];

    /// <summary>As the ledger stores them: rounded, unknown codes on Suspense, one line per account and party on its debit or credit side, zeros dropped.</summary>
    private static PostLine[] Normal(PostLine[] lines, Dictionary<string, int> accounts) =>
        GeneralLedger.Merge(lines.Select(l => l with { Code = accounts.ContainsKey(l.Code) ? l.Code : GlCodes.Suspense, Debit = Math.Round(l.Debit, 2), Credit = Math.Round(l.Credit, 2) }))
            .Where(l => l.Debit != 0 || l.Credit != 0).ToArray();

    private static bool Same(GlJournal j, Want w, Dictionary<int, string> codeOf)
    {
        if (j.Date != w.Date) return false;
        if (w.Frozen) return j.Memo == (w.Memo.Length > 300 ? w.Memo[..300] : w.Memo);
        static string Key(PostLine l) => $"{l.Code}|{l.Debit:0.00}|{l.Credit:0.00}|{l.CustomerId}|{l.SupplierId}";
        var have = GeneralLedger.Merge(j.Lines.Select(l => new PostLine(codeOf.GetValueOrDefault(l.AccountId, GlCodes.Suspense), l.Debit, l.Credit, l.CustomerId, l.SupplierId)))
            .Where(l => l.Debit != 0 || l.Credit != 0).Select(Key).Order();
        return have.SequenceEqual(w.Lines.Select(Key).Order());
    }

    private static string Cashbox(string? method) => string.Equals(method, "ALATPay", StringComparison.OrdinalIgnoreCase) ? GlCodes.AlatPayClearing : GlCodes.ForPaymentMethod(method);
}
