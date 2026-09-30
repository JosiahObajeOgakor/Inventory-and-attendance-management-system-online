using Inventory.Application.Abstractions;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Analytics;

/// <summary>
/// A customer who is overdue to buy again, measured against their OWN rhythm — not a fixed number of days for everybody.
/// <paramref name="TypicalDays"/> is the middle gap between their past orders, <paramref name="DaysSince"/> how long it has actually been.
/// </summary>
public sealed record ReorderDue(int CustomerId, string Customer, string? Phone, string? Email, int Orders, int TypicalDays, int DaysSince,
    int DaysOverdue, decimal AverageOrder, decimal LastOrderValue, DateOnly LastOrder, string UsualItems);

/// <summary>
/// Who is due to buy again. A customer with a settled habit ("20 bags every three weeks") is the easiest sale in the business, and the only way
/// to see they have gone quiet is to compare each one against their own pattern. Read-only, and it never guesses from fewer than three orders —
/// two purchases are not a rhythm.
/// </summary>
public sealed class ReorderDueQueries(IBusinessDbContext db, IClock clock)
{
    /// <summary>At least this many past orders before we claim to know someone's rhythm.</summary>
    private const int MinOrders = 3;
    /// <summary>How far past their usual gap counts as overdue. A tenth late is noise; a quarter late is worth a call.</summary>
    private const double OverdueFactor = 1.25;

    public async Task<List<ReorderDue>> DueAsync(int max, CancellationToken ct = default)
    {
        var today = clock.BusinessToday;
        var since = today.AddDays(-365);
        var rows = await (from i in db.Invoices.AsNoTracking()
                          join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id
                          where i.InvoiceDate >= since && i.Status != PaymentStatuses.Voided && !i.IsSample && c.CustomerType != CustomerTypes.WalkIn
                          select new { i.Id, i.CustomerId, c.Name, c.Phone, c.Email, i.InvoiceDate, i.TotalAmount }).ToListAsync(ct);

        var due = new List<ReorderDue>();
        foreach (var group in rows.GroupBy(r => r.CustomerId))
        {
            // One order per day: two invoices written the same afternoon are one shopping trip, not two points in a rhythm.
            var days = group.GroupBy(r => r.InvoiceDate).Select(g => new { Date = g.Key, Value = g.Sum(x => x.TotalAmount) })
                .OrderBy(x => x.Date).ToList();
            if (days.Count < MinOrders) continue;

            var gaps = days.Zip(days.Skip(1), (a, b) => b.Date.DayNumber - a.Date.DayNumber).Where(g => g > 0).ToList();
            if (gaps.Count == 0) continue;
            // The middle gap, not the average: one holiday-sized gap shouldn't stretch everyone's expected return.
            var sorted = gaps.OrderBy(g => g).ToList();
            var typical = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            if (typical <= 0) continue;

            var last = days[^1];
            var daysSince = today.DayNumber - last.Date.DayNumber;
            if (daysSince <= typical * OverdueFactor) continue;

            var first = group.First();
            due.Add(new ReorderDue(group.Key, first.Name, first.Phone, first.Email, days.Count, typical, daysSince,
                daysSince - typical, Math.Round(days.Average(d => d.Value), 2, MidpointRounding.ToEven), last.Value, last.Date,
                await UsualItemsAsync(group.Key, ct)));
        }

        // Most overdue relative to their own habit first, then by what they are worth.
        return due.OrderByDescending(d => (double)d.DaysOverdue / d.TypicalDays).ThenByDescending(d => d.AverageOrder)
            .Take(Math.Clamp(max, 1, 100)).ToList();
    }

    /// <summary>The two things this customer buys most often, so whoever calls them knows what to offer.</summary>
    private async Task<string> UsualItemsAsync(int customerId, CancellationToken ct)
    {
        var names = await (from it in db.InvoiceItems.AsNoTracking()
                           join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                           join p in db.Products.AsNoTracking() on it.ProductId equals p.Id
                           where i.CustomerId == customerId && i.Status != PaymentStatuses.Voided && !i.IsSample
                           group it.Quantity by p.Name into g
                           orderby g.Sum() descending
                           select g.Key).Take(2).ToListAsync(ct);
        return string.Join(", ", names);
    }
}
