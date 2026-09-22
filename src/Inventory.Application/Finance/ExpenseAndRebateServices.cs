using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Queries;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Finance;

public static class ExpenseCategories
{
    public static readonly string[] All = ["Rent", "Salaries", "Utilities", "Logistics", "Maintenance", "Other"];
}

public sealed record ExpenseInput(string Category, DateOnly? ExpenseDate, decimal Amount, string? Note);
public sealed record ExpenseDto(int Id, string Category, DateOnly ExpenseDate, decimal Amount, string? Note);
public sealed record ExpenseListDto(PagedResult<ExpenseDto> Page, decimal Total, IReadOnlyList<CategoryTotal> ByCategory);
public sealed record CategoryTotal(string Category, decimal Total);

public sealed class ExpenseInputValidator : AbstractValidator<ExpenseInput>
{
    public ExpenseInputValidator()
    {
        RuleFor(x => x.Category).Must(c => ExpenseCategories.All.Contains(c)).WithMessage("Choose one of the listed categories.");
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Enter an amount greater than zero.");
        RuleFor(x => x.Note).MaximumLength(250);
    }
}

public sealed class ExpenseService(IBusinessDbContext db, IClock clock)
{
    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<ExpenseListDto> ListAsync(DateOnly from, DateOnly to, string? category, PageRequest page, CancellationToken ct = default)
    {
        var q = db.Expenses.AsNoTracking().Where(e => e.ExpenseDate >= from && e.ExpenseDate <= to);
        if (!string.IsNullOrEmpty(category)) q = q.Where(e => e.Category == category);
        if (page.Term is { } t) q = q.Where(e => e.Category.Contains(t) || (e.Note != null && e.Note.Contains(t)));
        var total = await q.CountAsync(ct);
        var sum = await q.SumAsync(e => (decimal?)e.Amount, ct) ?? 0;
        var byCat = await q.GroupBy(e => e.Category).Select(g => new { g.Key, T = g.Sum(e => e.Amount) }).ToListAsync(ct);
        var rows = await q.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new ExpenseListDto(new PagedResult<ExpenseDto>(rows.Select(Map).ToList(), total, page.SafePage, page.SafeSize), sum,
            byCat.OrderByDescending(x => x.T).Select(x => new CategoryTotal(x.Key, x.T)).ToList());
    }

    private static ExpenseDto Map(Expense e) => new(e.Id, e.Category, e.ExpenseDate, e.Amount, e.Note);

    public async Task<int> CreateAsync(ExpenseInput i, CurrentUser user, CancellationToken ct = default)
    {
        await new ExpenseInputValidator().ValidateAndThrowAsync(i, ct);
        var e = new Expense { Category = i.Category, ExpenseDate = i.ExpenseDate ?? clock.BusinessToday, Amount = i.Amount, Note = N(i.Note), CreatedByUserId = user.Id };
        db.Expenses.Add(e);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "EXPENSE_CREATED", Entity = "Expense", EntityId = e.Id.ToString(), At = clock.UtcNow, Detail = $"{e.Category} {e.Amount:N2}" });
        await db.SaveChangesAsync(ct);
        return e.Id;
    }

    public async Task UpdateAsync(int id, ExpenseInput i, CurrentUser user, CancellationToken ct = default)
    {
        await new ExpenseInputValidator().ValidateAndThrowAsync(i, ct);
        var e = await db.Expenses.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Expense");
        e.Category = i.Category; e.Amount = i.Amount; e.Note = N(i.Note);
        if (i.ExpenseDate.HasValue) e.ExpenseDate = i.ExpenseDate.Value;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "EXPENSE_UPDATED", Entity = "Expense", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{e.Category} {e.Amount:N2}" });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CurrentUser user, CancellationToken ct = default)
    {
        var e = await db.Expenses.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Expense");
        db.Expenses.Remove(e);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "EXPENSE_DELETED", Entity = "Expense", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{e.Category} {e.Amount:N2} {e.Note}" });
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RebateCustomerDto(int CustomerId, string Customer, string Ranking, decimal Available, decimal Redeemed, decimal Lifetime);
public sealed record RebateSummaryDto(IReadOnlyList<RebateCustomerDto> Customers, decimal OutstandingTotal, decimal RedeemedTotal, decimal DefaultRatePct);
public sealed record RebateEntryDto(int Id, DateOnly EntryDate, string? InvoiceNumber, decimal Amount, string Status, DateOnly? RedeemedDate, string? Note);
public sealed record RedeemResult(string Customer, decimal Amount);

/// <summary>Rebates accrue on non-walk-in sales (see SalesService); this screen shows what is owed to each customer and marks it given back as goods.</summary>
public sealed class RebateService(IBusinessDbContext db, TransactionRunner tx, IClock clock, CompanyProfileService profile)
{
    public async Task<RebateSummaryDto> SummaryAsync(string? term, CancellationToken ct = default)
    {
        var agg = await db.RebateEntries.AsNoTracking().GroupBy(r => r.CustomerId)
            .Select(g => new { CustomerId = g.Key, Available = g.Where(r => r.Status == "Accrued").Sum(r => r.Amount), Redeemed = g.Where(r => r.Status == "Redeemed").Sum(r => r.Amount) }).ToListAsync(ct);
        var ids = agg.Select(a => a.CustomerId).ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var rows = agg.Where(a => customers.ContainsKey(a.CustomerId)).Select(a => new RebateCustomerDto(a.CustomerId, customers[a.CustomerId].Name, customers[a.CustomerId].CustomerType, a.Available, a.Redeemed, a.Available + a.Redeemed))
            .Where(r => r.Lifetime > 0).ToList();
        var outstanding = rows.Sum(r => r.Available); var redeemed = rows.Sum(r => r.Redeemed);
        if (!string.IsNullOrWhiteSpace(term)) rows = rows.Where(r => r.Customer.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) || r.Ranking.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return new RebateSummaryDto(rows.OrderByDescending(r => r.Available).ThenBy(r => r.Customer).ToList(), outstanding, redeemed, (await profile.GetAsync(ct)).DefaultRebateRatePct);
    }

    public async Task<List<RebateEntryDto>> EntriesAsync(int customerId, CancellationToken ct = default)
    {
        var rows = await (from r in db.RebateEntries.AsNoTracking().Where(x => x.CustomerId == customerId)
                          join i in db.Invoices.AsNoTracking() on r.InvoiceId equals i.Id into iss from i in iss.DefaultIfEmpty()
                          orderby r.Id descending
                          select new { r, Inv = i != null ? i.InvoiceNumber : null }).ToListAsync(ct);
        return rows.Select(x => new RebateEntryDto(x.r.Id, x.r.EntryDate, x.Inv, x.r.Amount, x.r.Status, x.r.RedeemedDate, x.r.Note)).ToList();
    }

    /// <summary>Marks every accrued entry as redeemed. The goods handed over are recorded separately, as in the desktop app.</summary>
    public Task<RedeemResult> RedeemAsync(int customerId, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var c = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, inner) ?? throw new NotFoundException("Customer");
            var entries = await db.RebateEntries.FromSqlInterpolated($"SELECT * FROM rebate_entries WHERE CustomerId = {customerId} AND Status = 'Accrued' FOR UPDATE").ToListAsync(inner);
            var total = entries.Sum(e => e.Amount);
            if (entries.Count == 0 || total <= 0) throw new BusinessRuleException($"{c.Name} has no rebate waiting to be collected.");
            foreach (var e in entries) { e.Status = "Redeemed"; e.RedeemedDate = clock.BusinessToday; }
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "REBATE_REDEEMED", Entity = "Customer", EntityId = customerId.ToString(), At = clock.UtcNow, Detail = $"{c.Name}: {total:N2}" });
            await db.SaveChangesAsync(inner);
            return new RedeemResult(c.Name, total);
        }, ct);

    public async Task SetDefaultRateAsync(decimal pct, CurrentUser user, CancellationToken ct = default)
    {
        if (pct is < 0 or > 100) throw new BusinessRuleException("The rate must be between 0 and 100.");
        (await profile.EnsureAsync(ct)).DefaultRebateRatePct = pct;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "REBATE_RATE_SET", Entity = "Company", At = clock.UtcNow, Detail = $"{pct}%" });
        await db.SaveChangesAsync(ct);
    }
}
