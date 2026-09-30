using Inventory.Application.Abstractions;
using Inventory.Application.Queries;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Purchasing;

public sealed record SupplyRowDto(int Id, string Reference, int SupplierId, string Supplier, DateOnly SupplyDate, decimal TotalAmount,
    decimal AmountPaid, decimal Outstanding, string PaymentStatus, int Lines, int Units, string? Note);
public sealed record SupplyItemDto(int? SupplierProductId, string Name, string? Size, string Unit, int Quantity, decimal UnitCost, decimal LineTotal);
public sealed record SupplyDetailDto(int Id, string Reference, int SupplierId, string Supplier, string? SupplierPhone, DateOnly SupplyDate,
    decimal TotalAmount, decimal AmountPaid, decimal Outstanding, string PaymentStatus, string? PaymentMethod, string? Note, string RecordedBy,
    IReadOnlyList<SupplyItemDto> Items);

public sealed record SupplierSupplyTotal(int SupplierId, string Supplier, decimal Amount, decimal Owed, int Records, int Units);
public sealed record SuppliedItemTotal(string Name, string? Size, string Unit, int Quantity, decimal Amount);
public sealed record SupplyMonth(int Year, int Month, decimal Amount);
public sealed record SupplierTrend(int SupplierId, string Supplier, IReadOnlyList<SupplyMonth> Months);
/// <summary>
/// The supply side of the dashboard, for the business currently open. Every figure comes from supply records alone — nothing here is mixed
/// with stock, sales or purchase orders.
/// </summary>
public sealed record SupplySummary(DateOnly AsOf, decimal ThisMonth, decimal LastMonth, decimal? ChangePct, decimal OwedTotal, int RecordsThisMonth,
    IReadOnlyList<SupplierSupplyTotal> BySupplier, IReadOnlyList<SuppliedItemTotal> TopItems, IReadOnlyList<SupplyMonth> Months,
    IReadOnlyList<SupplierTrend> Trends);

public sealed class SupplyQueries(IBusinessDbContext db, IClock clock, IUserDirectory users)
{
    public async Task<PagedResult<SupplyRowDto>> ListAsync(PageRequest page, int? supplierId, int? year, int? month, CancellationToken ct)
    {
        var q = from s in db.Supplies.AsNoTracking()
                join sup in db.Suppliers.AsNoTracking() on s.SupplierId equals sup.Id
                select new { s, Supplier = sup.Name };
        if (supplierId is int sid) q = q.Where(x => x.s.SupplierId == sid);
        if (year is int y && month is int m && m is >= 1 and <= 12)
        {
            var from = new DateOnly(y, m, 1); var to = from.AddMonths(1);
            q = q.Where(x => x.s.SupplyDate >= from && x.s.SupplyDate < to);
        }
        if (page.Term is { } t) q = q.Where(x => x.s.Reference.Contains(t) || x.Supplier.Contains(t));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.s.SupplyDate).ThenByDescending(x => x.s.Id)
            .Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize)
            .Select(x => new
            {
                x.s.Id, x.s.Reference, x.s.SupplierId, x.Supplier, x.s.SupplyDate, x.s.TotalAmount, x.s.AmountPaid, x.s.PaymentStatus, x.s.Note,
                Lines = x.s.Items.Count, Units = x.s.Items.Sum(i => (int?)i.Quantity) ?? 0,
            }).ToListAsync(ct);
        return new PagedResult<SupplyRowDto>(rows.Select(r => new SupplyRowDto(r.Id, r.Reference, r.SupplierId, r.Supplier, r.SupplyDate,
            r.TotalAmount, r.AmountPaid, Math.Max(0, r.TotalAmount - r.AmountPaid), r.PaymentStatus, r.Lines, r.Units, r.Note)).ToList(),
            total, page.SafePage, page.SafeSize);
    }

    public async Task<SupplyDetailDto?> GetAsync(int id, CancellationToken ct)
    {
        var s = await db.Supplies.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        var sup = await db.Suppliers.AsNoTracking().SingleAsync(x => x.Id == s.SupplierId, ct);
        var names = await users.NamesAsync([s.CreatedByUserId], ct);
        return new SupplyDetailDto(s.Id, s.Reference, sup.Id, sup.Name, sup.Phone, s.SupplyDate, s.TotalAmount, s.AmountPaid,
            Math.Max(0, s.TotalAmount - s.AmountPaid), s.PaymentStatus, s.PaymentMethod, s.Note, names.GetValueOrDefault(s.CreatedByUserId, ""),
            s.Items.Select(i => new SupplyItemDto(i.SupplierProductId, i.Name, i.Size, i.Unit, i.Quantity, i.UnitCost, i.LineTotal))
                .OrderBy(i => i.Name).ToList());
    }

    /// <summary>Everything the dashboard's supply panel shows: this month against last, who supplied what, and a 12-month trend.</summary>
    public async Task<SupplySummary> SummaryAsync(CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var nextStart = monthStart.AddMonths(1);
        var prevStart = monthStart.AddMonths(-1);
        var histStart = monthStart.AddMonths(-11);

        var rows = await (from s in db.Supplies.AsNoTracking().Where(x => x.SupplyDate >= histStart)
                          join sup in db.Suppliers.AsNoTracking() on s.SupplierId equals sup.Id
                          select new { s.Id, s.SupplierId, Supplier = sup.Name, s.SupplyDate, s.TotalAmount, s.AmountPaid }).ToListAsync(ct);
        // Owed is over ALL supplies, not just the last year: a debt older than that is still a debt.
        var owed = await db.Supplies.AsNoTracking().SumAsync(s => (decimal?)(s.TotalAmount - s.AmountPaid), ct) ?? 0m;

        decimal Between(DateOnly a, DateOnly b) => rows.Where(r => r.SupplyDate >= a && r.SupplyDate < b).Sum(r => r.TotalAmount);
        var thisMonth = Between(monthStart, nextStart);
        var lastMonth = Between(prevStart, monthStart);

        var inMonth = rows.Where(r => r.SupplyDate >= monthStart && r.SupplyDate < nextStart).ToList();
        var monthIds = inMonth.Select(r => r.Id).ToList();
        var monthItems = await db.SupplyItems.AsNoTracking().Where(i => monthIds.Contains(i.SupplyId))
            .Select(i => new { i.SupplyId, i.Name, i.Size, i.Unit, i.Quantity, i.LineTotal }).ToListAsync(ct);

        var unitsBySupply = monthItems.GroupBy(i => i.SupplyId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var bySupplier = inMonth.GroupBy(r => new { r.SupplierId, r.Supplier })
            .Select(g => new SupplierSupplyTotal(g.Key.SupplierId, g.Key.Supplier, g.Sum(r => r.TotalAmount),
                g.Sum(r => Math.Max(0, r.TotalAmount - r.AmountPaid)), g.Count(), g.Sum(r => unitsBySupply.GetValueOrDefault(r.Id))))
            .OrderByDescending(x => x.Amount).ToList();

        // Grouped by the name on the line, so two suppliers' own names for a similar thing stay separate — which is the point.
        var topItems = monthItems.GroupBy(i => new { i.Name, i.Size, i.Unit })
            .Select(g => new SuppliedItemTotal(g.Key.Name, g.Key.Size, g.Key.Unit, g.Sum(i => i.Quantity), g.Sum(i => i.LineTotal)))
            .OrderByDescending(x => x.Amount).Take(6).ToList();

        var months = Enumerable.Range(0, 12).Select(n => histStart.AddMonths(n))
            .Select(m => new SupplyMonth(m.Year, m.Month, Between(m, m.AddMonths(1)))).ToList();

        // A trend line for the suppliers that matter — the six biggest over the whole year, not just this month.
        var trends = rows.GroupBy(r => new { r.SupplierId, r.Supplier }).OrderByDescending(g => g.Sum(r => r.TotalAmount)).Take(6)
            .Select(g => new SupplierTrend(g.Key.SupplierId, g.Key.Supplier, Enumerable.Range(0, 12).Select(n => histStart.AddMonths(n))
                .Select(m => new SupplyMonth(m.Year, m.Month, g.Where(r => r.SupplyDate >= m && r.SupplyDate < m.AddMonths(1)).Sum(r => r.TotalAmount)))
                .ToList())).ToList();

        var change = lastMonth == 0 ? (decimal?)null : Math.Round((thisMonth - lastMonth) / Math.Abs(lastMonth) * 100m, 1, MidpointRounding.ToEven);
        return new SupplySummary(today, thisMonth, lastMonth, change, owed, inMonth.Count, bySupplier, topItems, months, trends);
    }
}
