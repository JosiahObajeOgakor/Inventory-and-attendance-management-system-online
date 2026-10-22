using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Budgets;

public sealed record BudgetSummaryDto(int Id, string Name, int Year, int? BranchId, string Status, decimal Income, decimal Expenses, decimal Profit, string? ApprovedBy);
public sealed record BudgetRowDto(string Code, string Account, string Type, decimal[] Months, decimal Total);
public sealed record BudgetDto(BudgetSummaryDto Summary, IReadOnlyList<BudgetRowDto> Rows);
public sealed record NewBudgetInput(string Name, int Year, int? BranchId, string Start, decimal GrowthPct);
public sealed record BudgetRowInput(string Code, decimal[] Months);
public sealed record VarianceRow(string Code, string Account, string Type, decimal Budget, decimal Actual, decimal Variance, decimal? VariancePct, bool Favourable);
public sealed record VarianceDto(int BudgetId, string Name, int Year, int ToMonth, IReadOnlyList<VarianceRow> Rows, decimal BudgetProfit, decimal ActualProfit);

/// <summary>
/// Budgets and budget-versus-actual. A budget is a year of monthly amounts for each income and expense account, for the whole business or one
/// branch; it can start empty, from last year's actuals, or from them plus a growth percentage. Variance compares it with the ledger month by month.
/// </summary>
public sealed class BudgetService(IBusinessDbContext db, IClock clock)
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private async Task<List<GlAccount>> PlAccountsAsync(CancellationToken ct) =>
        await db.GlAccounts.AsNoTracking().Where(a => a.IsActive && (a.Type == GlAccountTypes.Income || a.Type == GlAccountTypes.Expense)).OrderBy(a => a.Code).ToListAsync(ct);

    /// <summary>Actual movement per P&amp;L account per month for a year, in each account's natural direction (income as credit, expenses as debit).</summary>
    private async Task<Dictionary<(string Code, int Month), decimal>> ActualsAsync(int year, int? branchId, CancellationToken ct)
    {
        var first = new DateOnly(year, 1, 1); var last = new DateOnly(year, 12, 31);
        var hq = branchId is int b && await db.Branches.AnyAsync(x => x.Id == b && x.IsHeadOffice, ct);
        var rows = await (from l in db.GlLines join j in db.GlJournals on l.JournalId equals j.Id join a in db.GlAccounts on l.AccountId equals a.Id
                          where j.Status != GlJournalStatuses.Draft && j.Date >= first && j.Date <= last && (a.Type == GlAccountTypes.Income || a.Type == GlAccountTypes.Expense)
                                && (branchId == null || j.BranchId == branchId || (hq && j.BranchId == null))
                          group l by new { a.Code, a.Type, j.Date.Month } into g
                          select new { g.Key.Code, g.Key.Type, g.Key.Month, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync(ct);
        return rows.ToDictionary(r => (r.Code, r.Month), r => r.Type == GlAccountTypes.Income ? r.Cr - r.Dr : r.Dr - r.Cr);
    }

    public async Task<List<BudgetSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var budgets = await db.Budgets.AsNoTracking().Include(b => b.Lines).OrderByDescending(b => b.Year).ThenBy(b => b.Name).ToListAsync(ct);
        var types = (await PlAccountsAsync(ct)).ToDictionary(a => a.Code, a => a.Type);
        return budgets.Select(b => Summary(b, types)).ToList();
    }

    private static BudgetSummaryDto Summary(Budget b, Dictionary<string, string> types)
    {
        var income = b.Lines.Where(l => types.GetValueOrDefault(l.AccountCode) == GlAccountTypes.Income).Sum(l => l.Amount);
        var expenses = b.Lines.Where(l => types.GetValueOrDefault(l.AccountCode) == GlAccountTypes.Expense).Sum(l => l.Amount);
        return new BudgetSummaryDto(b.Id, b.Name, b.Year, b.BranchId, b.Status, income, expenses, income - expenses, b.ApprovedByName);
    }

    public async Task<BudgetDto> GetAsync(int id, CancellationToken ct = default)
    {
        var b = await db.Budgets.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Budget");
        var accounts = await PlAccountsAsync(ct);
        var rows = accounts.Select(a =>
        {
            var months = Enumerable.Range(1, 12).Select(m => b.Lines.Where(l => l.AccountCode == a.Code && l.Month == m).Sum(l => l.Amount)).ToArray();
            return new BudgetRowDto(a.Code, a.Name, a.Type, months, months.Sum());
        }).ToList();
        return new BudgetDto(Summary(b, accounts.ToDictionary(a => a.Code, a => a.Type)), rows);
    }

    public async Task<int> CreateAsync(NewBudgetInput i, CurrentUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Name the budget.");
        if (i.Year is < 2015 or > 2100) throw new BusinessRuleException("Choose a year.");
        if (i.BranchId is int bid && !await db.Branches.AnyAsync(x => x.Id == bid, ct)) throw new NotFoundException("Branch");
        if (i.Start is not ("empty" or "last-year")) throw new BusinessRuleException("Start empty or from last year's actuals.");
        if (i.GrowthPct is < -90 or > 500) throw new BusinessRuleException("Growth must be between −90% and 500%.");
        var b = new Budget { Name = i.Name.Trim(), Year = i.Year, BranchId = i.BranchId, CreatedByUserId = user.Id, CreatedAt = clock.UtcNow };
        if (i.Start == "last-year")
            foreach (var ((code, month), amount) in await ActualsAsync(i.Year - 1, i.BranchId, ct))
                if (amount != 0) b.Lines.Add(new BudgetLine { AccountCode = code, Month = month, Amount = R(amount * (1 + i.GrowthPct / 100m)) });
        db.Budgets.Add(b);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BUDGET_CREATED", Entity = "Budget", At = clock.UtcNow, Detail = $"{b.Name} {b.Year}" });
        await db.SaveChangesAsync(ct);
        return b.Id;
    }

    /// <summary>Replaces the given accounts' monthly amounts. An approved budget is locked (reopen it to change).</summary>
    public async Task SaveRowsAsync(int id, IReadOnlyList<BudgetRowInput> rows, CurrentUser user, CancellationToken ct = default)
    {
        var b = await db.Budgets.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Budget");
        if (b.Status == BudgetStatuses.Approved) throw new BusinessRuleException("This budget is approved. Reopen it to change it.");
        var codes = (await PlAccountsAsync(ct)).Select(a => a.Code).ToHashSet();
        foreach (var r in rows)
        {
            if (!codes.Contains(r.Code)) throw new BusinessRuleException($"{r.Code} isn't an income or expense account.");
            if (r.Months is not { Length: 12 }) throw new BusinessRuleException("Each row needs twelve monthly amounts.");
            b.Lines.RemoveAll(l => l.AccountCode == r.Code);
            for (var m = 1; m <= 12; m++)
                if (r.Months[m - 1] != 0) b.Lines.Add(new BudgetLine { AccountCode = r.Code, Month = m, Amount = R(r.Months[m - 1]) });
        }
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BUDGET_EDITED", Entity = "Budget", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{rows.Count} row(s)" });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(int id, bool approve, CurrentUser user, CancellationToken ct = default)
    {
        var b = await db.Budgets.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Budget");
        b.Status = approve ? BudgetStatuses.Approved : BudgetStatuses.Draft;
        b.ApprovedByName = approve ? user.FullName : null; b.ApprovedAt = approve ? clock.UtcNow : null;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = approve ? "BUDGET_APPROVED" : "BUDGET_REOPENED", Entity = "Budget", EntityId = id.ToString(), At = clock.UtcNow, Detail = b.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CurrentUser user, CancellationToken ct = default)
    {
        var b = await db.Budgets.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Budget");
        if (b.Status == BudgetStatuses.Approved) throw new BusinessRuleException("Reopen an approved budget before deleting it.");
        db.Budgets.Remove(b);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BUDGET_DELETED", Entity = "Budget", EntityId = id.ToString(), At = clock.UtcNow, Detail = b.Name });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Budget against actual for January to <paramref name="toMonth"/>. Favourable: income above budget, or expense below it.</summary>
    public async Task<VarianceDto> VarianceAsync(int id, int toMonth, CancellationToken ct = default)
    {
        if (toMonth is < 1 or > 12) throw new BusinessRuleException("Choose a month.");
        var b = await db.Budgets.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Budget");
        var accounts = await PlAccountsAsync(ct);
        var actuals = await ActualsAsync(b.Year, b.BranchId, ct);
        var rows = accounts.Select(a =>
        {
            var budget = b.Lines.Where(l => l.AccountCode == a.Code && l.Month <= toMonth).Sum(l => l.Amount);
            var actual = actuals.Where(x => x.Key.Code == a.Code && x.Key.Month <= toMonth).Sum(x => x.Value);
            var variance = actual - budget;
            var favourable = a.Type == GlAccountTypes.Income ? variance >= 0 : variance <= 0;
            return new VarianceRow(a.Code, a.Name, a.Type, budget, actual, variance, budget == 0 ? null : Math.Round(variance / budget * 100m, 1), favourable);
        }).Where(r => r.Budget != 0 || r.Actual != 0).ToList();
        decimal Profit(Func<VarianceRow, decimal> f) => rows.Where(r => r.Type == GlAccountTypes.Income).Sum(f) - rows.Where(r => r.Type == GlAccountTypes.Expense).Sum(f);
        return new VarianceDto(b.Id, b.Name, b.Year, toMonth, rows, Profit(r => r.Budget), Profit(r => r.Actual));
    }
}
