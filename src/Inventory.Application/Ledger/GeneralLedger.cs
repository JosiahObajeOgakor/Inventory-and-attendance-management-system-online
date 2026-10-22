using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Ledger;

/// <param name="Code">A chart-of-accounts code (Inventory.Domain.Entities.GlCodes).</param>
public sealed record PostLine(string Code, decimal Debit, decimal Credit, int? CustomerId = null, int? SupplierId = null, int? ProductId = null, string? Note = null)
{
    public static PostLine Dr(string code, decimal amount, int? customerId = null, int? supplierId = null, string? note = null) => new(code, amount, 0, customerId, supplierId, null, note);
    public static PostLine Cr(string code, decimal amount, int? customerId = null, int? supplierId = null, string? note = null) => new(code, 0, amount, customerId, supplierId, null, note);
}

/// <summary>
/// The double-entry engine. Business services call it INSIDE their own transaction, so a document and its journal are saved
/// together or not at all. Every journal balances; a journal for the same (source, id, event) is posted once however often it is replayed; nothing
/// posted is ever changed (a correction is a reversal); nothing posts into a locked month.
/// </summary>
public sealed class GeneralLedger(IBusinessDbContext db, IClock clock)
{
    /// <summary>
    /// The branch the next postings belong to. A service sets it from the warehouse involved (<see cref="ForWarehouseAsync"/>); otherwise the
    /// posting user's own branch is used; otherwise none (head office).
    /// </summary>
    public int? Branch { get; set; }

    /// <summary>Postings that follow belong to the branch of this warehouse.</summary>
    public async Task ForWarehouseAsync(int? warehouseId, CancellationToken ct)
    {
        if (warehouseId is not int w || w <= 0) return;
        Branch = await db.Warehouses.Where(x => x.Id == w).Select(x => x.BranchId).FirstOrDefaultAsync(ct);
    }
    /// <summary>
    /// Creates the standard chart the first time the books are used, and adds any standard account a later release introduced (existing books get
    /// the new payroll accounts, for example). Not cached: a rolled-back transaction must not leave stale ids behind.
    /// </summary>
    public async Task<Dictionary<string, int>> AccountsAsync(CancellationToken ct)
    {
        var have = await db.GlAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id, ct);
        var missing = GlCodes.Standard.Where(a => !have.ContainsKey(a.Code)).ToList();
        if (missing.Count == 0) return have;
        db.GlAccounts.AddRange(missing.Select(a => new GlAccount { Code = a.Code, Name = a.Name, Type = a.Type, IsControl = a.Control }));
        await db.SaveChangesAsync(ct);
        return await db.GlAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id, ct);
    }

    /// <summary>Posts a balanced journal (added to the current unit of work; the caller's SaveChanges writes it). Returns null when there is nothing to post.</summary>
    /// <param name="allowSoftClosed">Only a finance user allowed to close periods may post into a soft-closed month.</param>
    public async Task<GlJournal?> PostAsync(string sourceType, string sourceId, string sourceEvent, DateOnly date, string memo, IEnumerable<PostLine> lines,
        CurrentUser by, CancellationToken ct, bool allowSoftClosed = false, long? reversalOf = null)
    {
        var rows = lines.Select(l => l with { Debit = Math.Round(l.Debit, 2), Credit = Math.Round(l.Credit, 2) })
            .Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (rows.Count == 0) return null;
        if (rows.Any(l => l.Debit < 0 || l.Credit < 0)) throw new InvalidOperationException($"Negative amount on a journal line ({sourceType} {sourceId} {sourceEvent}).");
        var dr = rows.Sum(l => l.Debit); var cr = rows.Sum(l => l.Credit);
        if (dr != cr) throw new InvalidOperationException($"Unbalanced journal for {sourceType} {sourceId} {sourceEvent}: debits ₦{dr:N2}, credits ₦{cr:N2}.");

        if (await db.GlJournals.AnyAsync(j => j.SourceType == sourceType && j.SourceId == sourceId && j.SourceEvent == sourceEvent, ct)) return null;   // already posted
        await EnsureOpenAsync(date, allowSoftClosed, ct);

        var accounts = await AccountsAsync(ct);
        var journal = new GlJournal
        {
            Date = date, SourceType = sourceType, SourceId = sourceId, SourceEvent = sourceEvent, Memo = memo.Length > 300 ? memo[..300] : memo,
            Status = GlJournalStatuses.Posted, ReversalOfId = reversalOf, PreparedByUserId = by.Id == 0 ? null : by.Id, PreparedByName = by.FullName, CreatedAt = clock.UtcNow,
            BranchId = Branch ?? by.BranchId,
            Lines = rows.Select(l => new GlLine
            {
                // An unknown code never blocks the business document: the amount goes to Suspense, labelled, for the accountant to reclass.
                AccountId = accounts.TryGetValue(l.Code, out var id) ? id : accounts[GlCodes.Suspense],
                Debit = l.Debit, Credit = l.Credit, CustomerId = l.CustomerId, SupplierId = l.SupplierId, ProductId = l.ProductId,
                Note = accounts.ContainsKey(l.Code) ? l.Note : $"Unmapped account {l.Code}. {l.Note}".Trim(),
            }).ToList(),
        };
        db.GlJournals.Add(journal);
        return journal;
    }

    /// <summary>Posts the exact opposite of an earlier journal and marks it Reversed. Nothing if it was never posted or is already reversed.</summary>
    public async Task<GlJournal?> ReverseAsync(string sourceType, string sourceId, string sourceEvent, string reversalEvent, DateOnly date, string memo, CurrentUser by, CancellationToken ct)
    {
        var original = await db.GlJournals.Include(j => j.Lines)
            .SingleOrDefaultAsync(j => j.SourceType == sourceType && j.SourceId == sourceId && j.SourceEvent == sourceEvent && j.Status == GlJournalStatuses.Posted, ct);
        if (original is null) return null;
        var codes = (await AccountsAsync(ct)).ToDictionary(kv => kv.Value, kv => kv.Key);
        var reversal = await PostAsync(sourceType, sourceId, reversalEvent, date, memo,
            original.Lines.Select(l => new PostLine(codes[l.AccountId], l.Credit, l.Debit, l.CustomerId, l.SupplierId, l.ProductId, l.Note)), by, ct, reversalOf: original.Id);
        if (reversal is not null) { original.Status = GlJournalStatuses.Reversed; reversal.BranchId = original.BranchId; }   // a reversal belongs where the original did
        return reversal;
    }

    /// <summary>Reverses every still-posted journal of one document (an edited or deleted expense, say), so a new posting can replace them.</summary>
    public async Task ReverseAllAsync(string sourceType, string sourceId, DateOnly date, string memo, CurrentUser by, CancellationToken ct)
    {
        var events = await db.GlJournals.AsNoTracking()
            .Where(j => j.SourceType == sourceType && j.SourceId == sourceId && j.Status == GlJournalStatuses.Posted && j.ReversalOfId == null)
            .Select(j => j.SourceEvent).ToListAsync(ct);
        foreach (var e in events) await ReverseAsync(sourceType, sourceId, e, "reverse:" + e, date, memo, by, ct);
    }

    /// <summary>Nets lines on the same account and party into one debit or credit each.</summary>
    public static IEnumerable<PostLine> Merge(IEnumerable<PostLine> lines) =>
        lines.GroupBy(l => (l.Code, l.SupplierId, l.CustomerId)).Select(g =>
        {
            var net = g.Sum(l => l.Debit - l.Credit);
            return net >= 0 ? new PostLine(g.Key.Code, net, 0, g.Key.CustomerId, g.Key.SupplierId, null, g.First().Note)
                            : new PostLine(g.Key.Code, 0, -net, g.Key.CustomerId, g.Key.SupplierId, null, g.First().Note);
        });

    /// <summary>A unique event name for something that can happen many times to the same document (a second receipt, another edit).</summary>
    public static string Once(string prefix) => prefix + ":" + Guid.NewGuid().ToString("N");

    public async Task EnsureOpenAsync(DateOnly date, bool allowSoftClosed, CancellationToken ct)
    {
        var p = await db.GlPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.Year == date.Year && x.Month == date.Month, ct);
        if (p is null || p.Status == GlPeriodStatuses.Open) return;
        if (p.Status == GlPeriodStatuses.SoftClosed && allowSoftClosed) return;
        throw new BusinessRuleException($"The books for {date:MMMM yyyy} are closed. Date this in an open month, or ask a finance controller to reopen {date:MMMM yyyy}.");
    }
}
