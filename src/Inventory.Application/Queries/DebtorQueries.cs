using Inventory.Application.Abstractions;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Queries;

/// <param name="Owed">The customer's balance: everything they still owe (unpaid invoices and any opening balance).</param>
/// <param name="Overdue">The part of it on invoices past their due date.</param>
/// <param name="OldestUnpaid">Date of the oldest invoice not yet fully paid.</param>
/// <param name="DaysOutstanding">Days since that invoice.</param>
public sealed record DebtorDto(int CustomerId, string Name, string? Phone, string? Email, string CustomerType, decimal Owed, decimal Overdue, int UnpaidInvoices,
    DateOnly? OldestUnpaid, int? DaysOutstanding, DateOnly? LastPaymentOn, decimal? LastPaymentAmount, decimal CreditLimit);
/// <summary>One page of debtors plus the totals for everyone who owes (not just this page).</summary>
public sealed record DebtorsPage(IReadOnlyList<DebtorDto> Items, int Total, int Page, int PageSize, decimal TotalOwed, decimal TotalOverdue, int OverdueCustomers);

/// <summary>Everyone who owes the business money, biggest (or oldest) first.</summary>
public sealed class DebtorQueries(IBusinessDbContext db, IClock clock)
{
    public static readonly string[] Sorts = ["owed", "oldest", "overdue", "name"];

    public async Task<DebtorsPage> ListAsync(PageRequest page, string? sort, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var q = db.Customers.AsNoTracking().Where(c => c.Balance > 0);
        foreach (var w in SearchTerms.Words(page.Term)) q = q.Where(PartnerQueries.CustomerMatches(w));
        var customers = await q.Select(c => new { c.Id, c.Name, c.Phone, c.Email, c.CustomerType, c.Balance, c.CreditLimit }).ToListAsync(ct);
        var ids = customers.Select(c => c.Id).ToList();

        var open = await db.Invoices.AsNoTracking()
            .Where(i => ids.Contains(i.CustomerId) && i.VoidedAt == null && !i.IsSample && i.Status != PaymentStatuses.Voided && i.TotalAmount - i.AmountPaid > 0)
            .Select(i => new { i.CustomerId, i.InvoiceDate, i.DueDate, Left = i.TotalAmount - i.AmountPaid }).ToListAsync(ct);
        var byCustomer = open.GroupBy(i => i.CustomerId).ToDictionary(g => g.Key, g => g.ToList());
        var payments = await (from p in db.Payments.AsNoTracking()
                              join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                              where ids.Contains(i.CustomerId)
                              select new { i.CustomerId, p.PaymentDate, p.Amount }).ToListAsync(ct);
        var lastPaid = payments.GroupBy(p => p.CustomerId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PaymentDate).First());

        var rows = customers.Select(c =>
        {
            var inv = byCustomer.GetValueOrDefault(c.Id) ?? [];
            DateOnly? oldest = inv.Count == 0 ? null : inv.Min(i => i.InvoiceDate);
            var overdue = inv.Where(i => i.DueDate is DateOnly d && d < today).Sum(i => i.Left);
            var last = lastPaid.GetValueOrDefault(c.Id);
            return new DebtorDto(c.Id, c.Name, c.Phone, c.Email, c.CustomerType, c.Balance, Math.Min(overdue, c.Balance), inv.Count, oldest,
                oldest is DateOnly o ? today.DayNumber - o.DayNumber : null, last is null ? null : DateOnly.FromDateTime(last.PaymentDate.AddHours(1)), last?.Amount, c.CreditLimit);
        }).ToList();

        IOrderedEnumerable<DebtorDto> sorted = (sort ?? "owed") switch
        {
            "oldest" => rows.OrderByDescending(r => r.DaysOutstanding ?? -1).ThenByDescending(r => r.Owed),
            "overdue" => rows.OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Owed),
            "name" => rows.OrderBy(r => r.Name),
            _ => rows.OrderByDescending(r => r.Owed),
        };
        var items = sorted.ThenBy(r => r.CustomerId).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToList();
        return new DebtorsPage(items, rows.Count, page.SafePage, page.SafeSize, rows.Sum(r => r.Owed), rows.Sum(r => r.Overdue), rows.Count(r => r.Overdue > 0));
    }
}
