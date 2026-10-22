using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Ledger;

public sealed record AccountDto(int Id, string Code, string Name, string Type, bool IsControl, bool IsActive);
public sealed record AccountInput(string Code, string Name, string Type);
public sealed record BalanceRow(string Code, string Name, string Type, decimal Debit, decimal Credit, decimal Balance);
public sealed record TrialBalanceDto(DateOnly AsOf, IReadOnlyList<BalanceRow> Rows, decimal TotalDebit, decimal TotalCredit, bool Balanced);
public sealed record StatementLine(string Code, string Name, decimal Amount);
public sealed record ProfitAndLossDto(DateOnly From, DateOnly To, IReadOnlyList<StatementLine> Income, decimal TotalIncome,
    IReadOnlyList<StatementLine> Expenses, decimal TotalExpenses, decimal NetProfit);
public sealed record BalanceSheetDto(DateOnly AsOf, IReadOnlyList<StatementLine> Assets, decimal TotalAssets, IReadOnlyList<StatementLine> Liabilities,
    decimal TotalLiabilities, IReadOnlyList<StatementLine> Equity, decimal EarningsToDate, decimal TotalEquity, bool Balanced);
public sealed record LedgerLineDto(long JournalId, DateOnly Date, string Memo, string Source, decimal Debit, decimal Credit, decimal RunningBalance);
public sealed record JournalLineDto(string Code, string Account, decimal Debit, decimal Credit, string? Note);
public sealed record JournalDto(long Id, DateOnly Date, string Source, string Memo, string Status, long? ReversalOfId, string PreparedBy, string? ApprovedBy, IReadOnlyList<JournalLineDto> Lines);
public sealed record ManualLineInput(string Code, decimal Debit, decimal Credit, string? Note);
public sealed record ManualJournalInput(DateOnly Date, string Memo, List<ManualLineInput> Lines);
public sealed record OpeningBalancesInput(DateOnly Date, decimal CashOnHand, decimal Bank);
public sealed record PeriodDto(int Year, int Month, string Status, DateTime? ClosedAt, string? ClosedBy, string? Note);

/// <summary>
/// The books: chart of accounts, reports, manual journals (prepared by one person, approved by another), month close, and the opening balances
/// that move an existing business onto the ledger. Every figure is computed from posted journal lines; nothing is stored twice.
/// </summary>
public sealed class AccountingService(IBusinessDbContext db, TransactionRunner tx, IClock clock, GeneralLedger gl)
{
    // ------------------------------------------------------------------ chart of accounts
    public async Task<IReadOnlyList<AccountDto>> AccountsAsync(CancellationToken ct)
    {
        await gl.AccountsAsync(ct);
        return await db.GlAccounts.AsNoTracking().OrderBy(a => a.Code).Select(a => new AccountDto(a.Id, a.Code, a.Name, a.Type, a.IsControl, a.IsActive)).ToListAsync(ct);
    }

    public async Task<int> AddAccountAsync(AccountInput i, CurrentUser user, CancellationToken ct)
    {
        var code = (i.Code ?? "").Trim();
        if (code.Length is 0 or > 10 || !code.All(char.IsAsciiLetterOrDigit)) throw new BusinessRuleException("Use a code of up to 10 letters or digits.");
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Give the account a name.");
        if (!GlAccountTypes.All.Contains(i.Type)) throw new BusinessRuleException("Choose Asset, Liability, Equity, Income or Expense.");
        await gl.AccountsAsync(ct);
        if (await db.GlAccounts.AnyAsync(a => a.Code == code, ct)) throw new BusinessRuleException($"Account {code} already exists.");
        var a = new GlAccount { Code = code, Name = i.Name.Trim(), Type = i.Type };
        db.GlAccounts.Add(a);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "GL_ACCOUNT_ADDED", Entity = "GlAccount", EntityId = code, At = clock.UtcNow, Detail = $"{code} {a.Name} ({a.Type})" });
        await db.SaveChangesAsync(ct);
        return a.Id;
    }

    // ------------------------------------------------------------------ reports
    private int? branchFilter;
    private bool includeUnassigned;

    /// <summary>Limits the statements that follow to one branch (head office also takes everything not tied to a branch). Null = the whole business.</summary>
    public async Task ForBranchAsync(int? branchId, CancellationToken ct)
    {
        branchFilter = branchId;
        includeUnassigned = branchId is int b && await db.Branches.AnyAsync(x => x.Id == b && x.IsHeadOffice, ct);
    }

    private IQueryable<GlLine> PostedLines(DateOnly? start, DateOnly end) =>
        from l in db.GlLines.AsNoTracking()
        join j in db.GlJournals.AsNoTracking() on l.JournalId equals j.Id
        where j.Status != GlJournalStatuses.Draft && j.Date <= end && (start == null || j.Date >= start)
              && (branchFilter == null || j.BranchId == branchFilter || (includeUnassigned && j.BranchId == null))
        select l;

    private async Task<List<BalanceRow>> BalancesAsync(DateOnly? from, DateOnly to, CancellationToken ct)
    {
        await gl.AccountsAsync(ct);
        var sums = await PostedLines(from, to).GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, Dr = g.Sum(l => l.Debit), Cr = g.Sum(l => l.Credit) }).ToDictionaryAsync(x => x.Key, ct);
        var accounts = await db.GlAccounts.AsNoTracking().OrderBy(a => a.Code).ToListAsync(ct);
        return accounts.Select(a =>
        {
            var s = sums.GetValueOrDefault(a.Id);
            decimal dr = s?.Dr ?? 0, cr = s?.Cr ?? 0;
            return new BalanceRow(a.Code, a.Name, a.Type, dr, cr, GlAccountTypes.DebitNormal(a.Type) ? dr - cr : cr - dr);
        }).ToList();
    }

    public async Task<TrialBalanceDto> TrialBalanceAsync(DateOnly asOf, CancellationToken ct)
    {
        var rows = (await BalancesAsync(null, asOf, ct)).Where(r => r.Debit != 0 || r.Credit != 0).ToList();
        decimal dr = rows.Sum(r => r.Debit), cr = rows.Sum(r => r.Credit);
        return new TrialBalanceDto(asOf, rows, dr, cr, dr == cr);
    }

    /// <summary>Every account's movement within a period (for income and expense: the period's profit and loss lines).</summary>
    public Task<List<BalanceRow>> TrialBalanceForPeriodAsync(DateOnly from, DateOnly to, CancellationToken ct) => BalancesAsync(from, to, ct);

    public async Task<ProfitAndLossDto> ProfitAndLossAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await BalancesAsync(from, to, ct);
        var income = rows.Where(r => r.Type == GlAccountTypes.Income && r.Balance != 0).Select(r => new StatementLine(r.Code, r.Name, r.Balance)).ToList();
        var expenses = rows.Where(r => r.Type == GlAccountTypes.Expense && r.Balance != 0).Select(r => new StatementLine(r.Code, r.Name, r.Balance)).ToList();
        decimal ti = income.Sum(x => x.Amount), te = expenses.Sum(x => x.Amount);
        return new ProfitAndLossDto(from, to, income, ti, expenses, te, ti - te);
    }

    /// <summary>Assets = liabilities + equity, where equity includes all profit earned to date (closing entries are not needed to read it).</summary>
    public async Task<BalanceSheetDto> BalanceSheetAsync(DateOnly asOf, CancellationToken ct)
    {
        var rows = await BalancesAsync(null, asOf, ct);
        StatementLine L(BalanceRow r) => new(r.Code, r.Name, r.Balance);
        var assets = rows.Where(r => r.Type == GlAccountTypes.Asset && r.Balance != 0).Select(L).ToList();
        var liabilities = rows.Where(r => r.Type == GlAccountTypes.Liability && r.Balance != 0).Select(L).ToList();
        var equity = rows.Where(r => r.Type == GlAccountTypes.Equity && r.Balance != 0).Select(L).ToList();
        var earnings = rows.Where(r => r.Type == GlAccountTypes.Income).Sum(r => r.Balance) - rows.Where(r => r.Type == GlAccountTypes.Expense).Sum(r => r.Balance);
        decimal ta = assets.Sum(x => x.Amount), tl = liabilities.Sum(x => x.Amount), te = equity.Sum(x => x.Amount) + earnings;
        return new BalanceSheetDto(asOf, assets, ta, liabilities, tl, equity, earnings, te, ta == tl + te);
    }

    public async Task<IReadOnlyList<LedgerLineDto>> AccountLedgerAsync(string code, DateOnly start, DateOnly end, CancellationToken ct)
    {
        await gl.AccountsAsync(ct);
        var a = await db.GlAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, ct) ?? throw new NotFoundException("Account");
        var debitNormal = GlAccountTypes.DebitNormal(a.Type);
        var before = await PostedLines(null, start.AddDays(-1)).Where(l => l.AccountId == a.Id).SumAsync(l => (decimal?)(debitNormal ? l.Debit - l.Credit : l.Credit - l.Debit), ct) ?? 0;
        var lines = await (from l in db.GlLines.AsNoTracking()
                           join j in db.GlJournals.AsNoTracking() on l.JournalId equals j.Id
                           where l.AccountId == a.Id && j.Status != GlJournalStatuses.Draft && j.Date >= start && j.Date <= end
                           orderby j.Date, j.Id
                           select new { j.Id, j.Date, j.Memo, j.SourceType, j.SourceId, l.Debit, l.Credit }).ToListAsync(ct);
        var running = before;
        return lines.Select(x =>
        {
            running += debitNormal ? x.Debit - x.Credit : x.Credit - x.Debit;
            return new LedgerLineDto(x.Id, x.Date, x.Memo, $"{x.SourceType} {x.SourceId}", x.Debit, x.Credit, running);
        }).ToList();
    }

    public async Task<IReadOnlyList<JournalDto>> JournalsAsync(DateOnly from, DateOnly to, string? status, CancellationToken ct)
    {
        var accounts = await db.GlAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var q = db.GlJournals.AsNoTracking().Include(j => j.Lines).Where(j => j.Date >= from && j.Date <= to);
        if (!string.IsNullOrEmpty(status)) q = q.Where(j => j.Status == status);
        var list = await q.OrderByDescending(j => j.Date).ThenByDescending(j => j.Id).Take(500).ToListAsync(ct);
        return list.Select(j => new JournalDto(j.Id, j.Date, $"{j.SourceType} {j.SourceId} {j.SourceEvent}", j.Memo, j.Status, j.ReversalOfId, j.PreparedByName, j.ApprovedByName,
            j.Lines.Select(l => new JournalLineDto(accounts[l.AccountId].Code, accounts[l.AccountId].Name, l.Debit, l.Credit, l.Note)).ToList())).ToList();
    }

    // ------------------------------------------------------------------ manual journals (maker-checker)
    /// <summary>Saves a balanced manual journal as a Draft. Control accounts (receivables, payables, inventory…) are posted only by the system.</summary>
    public async Task<long> DraftAsync(ManualJournalInput i, CurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Memo)) throw new BusinessRuleException("Explain the journal in the narration.");
        var lines = (i.Lines ?? []).Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (lines.Count < 2) throw new BusinessRuleException("A journal needs at least two lines.");
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0))) throw new BusinessRuleException("Each line is either a debit or a credit, never negative.");
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit)) throw new BusinessRuleException("Debits and credits must be equal.");
        await gl.AccountsAsync(ct);
        var accounts = await db.GlAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, ct);
        foreach (var l in lines)
        {
            if (!accounts.TryGetValue(l.Code, out var a) || !a.IsActive) throw new BusinessRuleException($"Account {l.Code} doesn't exist.");
            if (a.IsControl) throw new BusinessRuleException($"{a.Code} {a.Name} is kept by the system from documents; correct it through the document instead.");
        }
        var j = new GlJournal
        {
            Date = i.Date, SourceType = "Manual", SourceId = Guid.NewGuid().ToString("N")[..12], SourceEvent = "journal", Memo = i.Memo.Trim(), Status = GlJournalStatuses.Draft,
            PreparedByUserId = user.Id, PreparedByName = user.FullName, CreatedAt = clock.UtcNow,
            Lines = lines.Select(l => new GlLine { AccountId = accounts[l.Code].Id, Debit = Math.Round(l.Debit, 2), Credit = Math.Round(l.Credit, 2), Note = l.Note }).ToList(),
        };
        db.GlJournals.Add(j);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "GL_JOURNAL_DRAFTED", Entity = "GlJournal", At = clock.UtcNow, Detail = j.Memo });
        await db.SaveChangesAsync(ct);
        return j.Id;
    }

    /// <summary>Posts a draft. The approver can't be the preparer; a soft-closed month needs a period-close user.</summary>
    public Task ApproveAsync(long id, CurrentUser approver, bool mayPostSoftClosed, CancellationToken ct) =>
        tx.RunAsync<bool>(async inner =>
        {
            var j = await db.GlJournals.FromSqlInterpolated($"SELECT * FROM gl_journals WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Journal");
            if (j.Status != GlJournalStatuses.Draft) throw new BusinessRuleException("Only a draft journal can be approved.");
            if (j.PreparedByUserId == approver.Id) throw new ForbiddenActionException("You prepared this journal; another person must approve it.");
            await gl.EnsureOpenAsync(j.Date, mayPostSoftClosed, inner);
            j.Status = GlJournalStatuses.Posted; j.ApprovedByUserId = approver.Id; j.ApprovedByName = approver.FullName;
            db.AuditLogs.Add(new AuditLog { UserId = approver.Id, UserName = approver.FullName, Action = "GL_JOURNAL_POSTED", Entity = "GlJournal", EntityId = id.ToString(), At = clock.UtcNow, Detail = j.Memo });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    /// <summary>Corrects a posted manual journal by posting its exact opposite (never by editing it).</summary>
    public Task ReverseManualAsync(long id, string reason, CurrentUser user, CancellationToken ct) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("Give a reason for the reversal.");
            var j = await db.GlJournals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, inner) ?? throw new NotFoundException("Journal");
            if (j.SourceType != "Manual") throw new BusinessRuleException("Journals made by documents are corrected through the document (void, credit note, edit).");
            if (await gl.ReverseAsync(j.SourceType, j.SourceId, j.SourceEvent, "reversal", clock.BusinessToday, $"Reversal: {reason.Trim()}", user, inner) is null)
                throw new BusinessRuleException("Only a posted journal can be reversed.");
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    // ------------------------------------------------------------------ periods
    public async Task<IReadOnlyList<PeriodDto>> PeriodsAsync(CancellationToken ct) =>
        await db.GlPeriods.AsNoTracking().OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .Select(p => new PeriodDto(p.Year, p.Month, p.Status, p.ClosedAt, p.ClosedBy, p.Note)).ToListAsync(ct);

    /// <summary>Sets a month's status. Closing needs a clean trial balance; reopening needs a reason (both audited).</summary>
    public async Task SetPeriodAsync(int year, int month, string status, string? note, CurrentUser user, CancellationToken ct)
    {
        if (month is < 1 or > 12) throw new BusinessRuleException("Month must be 1 to 12.");
        if (status is not (GlPeriodStatuses.Open or GlPeriodStatuses.SoftClosed or GlPeriodStatuses.Locked)) throw new BusinessRuleException("Unknown period status.");
        var p = await db.GlPeriods.SingleOrDefaultAsync(x => x.Year == year && x.Month == month, ct);
        if (status == GlPeriodStatuses.Open && p is not null && p.Status != GlPeriodStatuses.Open && string.IsNullOrWhiteSpace(note))
            throw new BusinessRuleException("Give a reason for reopening a closed month.");
        if (status != GlPeriodStatuses.Open)
        {
            var end = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
            if (!(await TrialBalanceAsync(end, ct)).Balanced) throw new BusinessRuleException("The trial balance doesn't balance; fix it before closing the month.");
        }
        if (p is null) db.GlPeriods.Add(p = new GlPeriod { Year = year, Month = month });
        p.Status = status; p.Note = note?.Trim();
        p.ClosedAt = status == GlPeriodStatuses.Open ? null : clock.UtcNow; p.ClosedBy = status == GlPeriodStatuses.Open ? null : user.FullName;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "GL_PERIOD_SET", Entity = "GlPeriod", EntityId = $"{year}-{month:00}", At = clock.UtcNow, Detail = $"{status} {note}".Trim() });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ moving an existing business onto the ledger
    /// <summary>
    /// One opening journal from today's running balances: what each customer owes, what is owed to each supplier, stock at cost, and the cash and bank
    /// the owner confirms. The difference is the owner's equity. Can be posted once.
    /// </summary>
    public Task<long> PostOpeningBalancesAsync(OpeningBalancesInput i, CurrentUser user, CancellationToken ct) =>
        tx.RunAsync(async inner =>
        {
            if (await db.GlJournals.AnyAsync(j => j.SourceType == "Opening", inner)) throw new BusinessRuleException("Opening balances have already been posted.");
            if (i.CashOnHand < 0 || i.Bank < 0) throw new BusinessRuleException("Cash and bank can't be negative.");
            var customers = await db.Customers.AsNoTracking().Where(c => c.Balance != 0).Select(c => new { c.Id, c.Balance }).ToListAsync(inner);
            var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.Balance != 0).Select(s => new { s.Id, s.Balance }).ToListAsync(inner);
            var stockValue = await (from b in db.StockBatches join p in db.Products on b.ProductId equals p.Id select b.QuantityOnHand * p.CostPrice).SumAsync(x => (decimal?)x, inner) ?? 0;

            // Books that already have entries (the ledger was switched on after trading began): bring each control account to the records it
            // summarises, posting only the difference. A fresh ledger is the same thing with nothing posted yet.
            var balances = (await BalancesAsync(null, DateOnly.MaxValue, inner)).ToDictionary(r => r.Code, r => r.Balance);
            decimal Has(string code) => balances.GetValueOrDefault(code);
            var stockGap = stockValue - Has(GlCodes.Inventory);
            var lines = new List<PostLine> { PostLine.Dr(GlCodes.Cash, i.CashOnHand), PostLine.Dr(GlCodes.Bank, i.Bank),
                stockGap >= 0 ? PostLine.Dr(GlCodes.Inventory, stockGap) : PostLine.Cr(GlCodes.Inventory, -stockGap) };
            if (Has(GlCodes.Receivables) == 0 && Has(GlCodes.CustomerDeposits) == 0)
                lines.AddRange(customers.Select(c => c.Balance > 0 ? PostLine.Dr(GlCodes.Receivables, c.Balance, customerId: c.Id) : PostLine.Cr(GlCodes.CustomerDeposits, -c.Balance, customerId: c.Id)));
            if (Has(GlCodes.Payables) == 0)
                lines.AddRange(suppliers.Select(s => s.Balance > 0 ? PostLine.Cr(GlCodes.Payables, s.Balance, supplierId: s.Id) : PostLine.Dr(GlCodes.Payables, -s.Balance, supplierId: s.Id)));
            var gap = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
            lines.Add(gap >= 0 ? PostLine.Cr(GlCodes.OwnersEquity, gap) : PostLine.Dr(GlCodes.OwnersEquity, -gap));

            var j = await gl.PostAsync("Opening", "1", "opening", i.Date, "Opening balances", lines, user, inner)
                ?? throw new BusinessRuleException("There is nothing to open the books with.");
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "GL_OPENED", Entity = "GlJournal", At = clock.UtcNow, Detail = $"Stock ₦{stockValue:N2}, equity ₦{gap:N2}" });
            await db.SaveChangesAsync(inner);
            return j.Id;
        }, ct);

    /// <summary>The control figure an accountant checks: the Inventory account against stock on hand at cost. A difference means stock moved without a posting.</summary>
    public async Task<(decimal Ledger, decimal Stock)> InventoryReconciliationAsync(CancellationToken ct)
    {
        var ledger = (await BalancesAsync(null, clock.BusinessToday, ct)).Single(r => r.Code == GlCodes.Inventory).Balance;
        var stock = await (from b in db.StockBatches join p in db.Products on b.ProductId equals p.Id select b.QuantityOnHand * p.CostPrice).SumAsync(x => (decimal?)x, ct) ?? 0;
        return (ledger, stock);
    }
}
