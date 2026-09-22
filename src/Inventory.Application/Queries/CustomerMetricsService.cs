using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Queries;

public sealed record MonthTotal(int Year, int Month, decimal Total, int Invoices);
public sealed record TopProduct(int ProductId, string Product, int Quantity, decimal Revenue);
public sealed record CustomerMetricsDto(int CustomerId, string Name, string Ranking, decimal TrailingTwelveMonths, decimal LifetimeSpend, int InvoiceCount,
    decimal AverageOrder, DateOnly? LastPurchase, decimal Balance, IReadOnlyList<MonthTotal> Monthly, IReadOnlyList<TopProduct> TopProducts);

/// <summary>What a customer is worth: twelve months of spend, favourite products and average order. Voided sales never count.</summary>
public sealed class CustomerMetricsService(IBusinessDbContext db, IClock clock)
{
    public async Task<CustomerMetricsDto?> GetAsync(int customerId, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct);
        if (c is null) return null;
        var today = clock.BusinessToday;
        var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var invoices = await db.Invoices.AsNoTracking().Where(i => i.CustomerId == customerId && i.Status != PaymentStatuses.Voided && !i.IsSample)
            .Select(i => new { i.Id, i.InvoiceDate, i.TotalAmount }).ToListAsync(ct);
        var monthly = Enumerable.Range(0, 12).Select(n => start.AddMonths(n)).Select(m =>
        {
            var inMonth = invoices.Where(i => i.InvoiceDate.Year == m.Year && i.InvoiceDate.Month == m.Month).ToList();
            return new MonthTotal(m.Year, m.Month, inMonth.Sum(i => i.TotalAmount), inMonth.Count);
        }).ToList();
        var trailing = monthly.Sum(m => m.Total);
        var ids = invoices.Select(i => i.Id).ToList();
        var top = ids.Count == 0 ? [] : await (from it in db.InvoiceItems.AsNoTracking().Where(x => ids.Contains(x.InvoiceId)) join p in db.Products.AsNoTracking() on it.ProductId equals p.Id
                                              group it by new { it.ProductId, p.Name } into g
                                              select new { g.Key.ProductId, g.Key.Name, Q = g.Sum(x => x.Quantity), R = g.Sum(x => x.LineTotal) }).OrderByDescending(x => x.R).Take(5).ToListAsync(ct);
        var lifetime = invoices.Sum(i => i.TotalAmount);
        return new CustomerMetricsDto(c.Id, c.Name, CustomerRanking.For(trailing), trailing, lifetime, invoices.Count, invoices.Count == 0 ? 0 : Money.Round(lifetime / invoices.Count),
            invoices.Count == 0 ? null : invoices.Max(i => i.InvoiceDate), c.Balance, monthly, top.Select(t => new TopProduct(t.ProductId, t.Name, t.Q, t.R)).ToList());
    }
}
