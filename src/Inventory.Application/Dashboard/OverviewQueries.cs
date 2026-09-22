using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Dashboard;

public sealed record Kpi(decimal Current, decimal Previous, decimal? ChangePct);
public sealed record DayPoint(DateOnly Date, decimal Revenue, decimal Collected, decimal GrossProfit, decimal Expenses);
public sealed record MonthPoint(int Year, int Month, decimal Revenue, decimal GrossProfit, decimal Expenses);
public sealed record WarehouseStock(int Id, string Name, int Units, decimal CostValue, decimal RetailValue, int Products, int LowBatches, int ExpiredUnits, int ExpiringSoonUnits);
public sealed record CustomerMonth(int Year, int Month, decimal Sales, decimal GrossProfit, decimal Profitability);
public sealed record CustomerHero(int Id, string Name, string Ranking, decimal OpenBalance, IReadOnlyList<CustomerMonth> Months);
public sealed record DueItem(int InvoiceId, string InvoiceNumber, int CustomerId, string Customer, string? Phone, decimal Outstanding, DateOnly DueDate, int DaysOverdue);
public sealed record ProductMover(int ProductId, string Name, int Quantity, decimal Revenue, int? DaysSinceLastSale, int InStock, decimal StockValue);
public sealed record Signal(string Kind, string Severity, string Title, string Detail, decimal? Amount);
public sealed record Overview(
    DateOnly AsOf, Kpi Revenue, Kpi GrossProfit, Kpi NetProfit, Kpi Losses, Kpi Collected, decimal InventoryValue, int Customers, int NewCustomersThisMonth,
    decimal ReceivablesTotal, decimal OverdueTotal, IReadOnlyList<DayPoint> Days, IReadOnlyList<MonthPoint> Months, IReadOnlyList<WarehouseStock> Warehouses,
    IReadOnlyList<CustomerHero> TopCustomers, IReadOnlyList<ProductMover> TopProducts, IReadOnlyList<ProductMover> SlowMovers, IReadOnlyList<Signal> Signals);
public sealed record Calendar(int Year, int Month, IReadOnlyList<DueItem> Due, IReadOnlyList<DueItem> Overdue);

/// <summary>
/// Everything the admin dashboard and the assistant need, computed from this company's own records. Nothing here is estimated by a model:
/// figures are sums over invoices, payments, expenses and stock; "signals" are plain rules over those figures.
/// Revenue is the sum of line totals before the invoice-level discount (the same definition as the Finance screen).
/// </summary>
public sealed class OverviewQueries(IBusinessDbContext db, IClock clock)
{
    private static Kpi K(decimal cur, decimal prev) => new(cur, prev, prev == 0 ? null : Math.Round((cur - prev) / Math.Abs(prev) * 100m, 1, MidpointRounding.ToEven));
    private static DateTime LagosDayStartUtc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).AddHours(-1);

    public async Task<Overview> GetAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 90);
        var today = clock.BusinessToday;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var prevStart = monthStart.AddMonths(-1);
        var histStart = monthStart.AddMonths(-11);
        var dayStart = today.AddDays(-(days - 1));

        // ---- sales lines for the last 12 months (one pass, everything else is grouped in memory)
        var lines = await (from it in db.InvoiceItems.AsNoTracking()
                           join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                           where i.InvoiceDate >= histStart && i.Status != PaymentStatuses.Voided && !i.IsSample
                           select new { i.Id, i.CustomerId, i.InvoiceDate, it.ProductId, it.Quantity, Line = it.LineTotal, Cost = it.Quantity * it.UnitCost }).ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.ExpenseDate >= histStart).Select(e => new { e.ExpenseDate, e.Amount }).ToListAsync(ct);
        var histUtc = LagosDayStartUtc(histStart);
        var payments = await (from p in db.Payments.AsNoTracking() join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                              where p.PaymentDate >= histUtc && i.Status != PaymentStatuses.Voided select new { p.PaymentDate, p.Amount }).ToListAsync(ct);

        decimal Rev(DateOnly a, DateOnly b) => lines.Where(l => l.InvoiceDate >= a && l.InvoiceDate < b).Sum(l => l.Line);
        decimal Gp(DateOnly a, DateOnly b) => lines.Where(l => l.InvoiceDate >= a && l.InvoiceDate < b).Sum(l => l.Line - l.Cost);
        decimal Exp(DateOnly a, DateOnly b) => expenses.Where(e => e.ExpenseDate >= a && e.ExpenseDate < b).Sum(e => e.Amount);
        decimal Col(DateOnly a, DateOnly b) => payments.Where(p => DateOnly.FromDateTime(p.PaymentDate.AddHours(1)) >= a && DateOnly.FromDateTime(p.PaymentDate.AddHours(1)) < b).Sum(p => p.Amount);
        var nextStart = monthStart.AddMonths(1);

        // ---- losses: value of sales voided + stock written off at cost (this month vs last)
        async Task<decimal> LossesAsync(DateOnly a, DateOnly b)
        {
            var fromUtc = LagosDayStartUtc(a); var toUtc = LagosDayStartUtc(b);
            var voids = await db.Invoices.AsNoTracking().Where(i => i.Status == PaymentStatuses.Voided && i.VoidedAt >= fromUtc && i.VoidedAt < toUtc).SumAsync(i => (decimal?)i.TotalAmount, ct) ?? 0;
            var writeOff = await (from m in db.StockMovements.AsNoTracking() join p in db.Products.AsNoTracking() on m.ProductId equals p.Id
                                  where m.MovementType == MovementTypes.Adjust && m.Quantity < 0 && m.MovementDate >= fromUtc && m.MovementDate < toUtc
                                  select (decimal?)(-m.Quantity * p.CostPrice)).SumAsync(ct) ?? 0;
            return voids + writeOff;
        }

        var revenue = K(Rev(monthStart, nextStart), Rev(prevStart, monthStart));
        var gross = K(Gp(monthStart, nextStart), Gp(prevStart, monthStart));
        var net = K(Gp(monthStart, nextStart) - Exp(monthStart, nextStart), Gp(prevStart, monthStart) - Exp(prevStart, monthStart));
        var losses = K(await LossesAsync(monthStart, nextStart), await LossesAsync(prevStart, monthStart));
        var collected = K(Col(monthStart, nextStart), Col(prevStart, monthStart));

        var dayPoints = Enumerable.Range(0, days).Select(n => dayStart.AddDays(n)).Select(d =>
            new DayPoint(d, Rev(d, d.AddDays(1)), Col(d, d.AddDays(1)), Gp(d, d.AddDays(1)), Exp(d, d.AddDays(1)))).ToList();
        var monthPoints = Enumerable.Range(0, 12).Select(n => histStart.AddMonths(n)).Select(m =>
            new MonthPoint(m.Year, m.Month, Rev(m, m.AddMonths(1)), Gp(m, m.AddMonths(1)), Exp(m, m.AddMonths(1)))).ToList();

        // ---- stock by warehouse
        var batches = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                             select new { b.WarehouseId, b.ProductId, b.QuantityOnHand, b.ExpiryDate, p.CostPrice, p.PriceRetail, p.ReorderLevel }).ToListAsync(ct);
        var whs = await db.Warehouses.AsNoTracking().OrderBy(w => w.Id).ToListAsync(ct);
        var soon = today.AddDays(30);
        var warehouses = whs.Select(w =>
        {
            var bs = batches.Where(b => b.WarehouseId == w.Id && b.QuantityOnHand > 0).ToList();
            return new WarehouseStock(w.Id, w.Name, bs.Sum(b => b.QuantityOnHand), bs.Sum(b => b.QuantityOnHand * b.CostPrice), bs.Sum(b => b.QuantityOnHand * b.PriceRetail),
                bs.Select(b => b.ProductId).Distinct().Count(), batches.Count(b => b.WarehouseId == w.Id && b.QuantityOnHand <= b.ReorderLevel),
                bs.Where(b => b.ExpiryDate < today).Sum(b => b.QuantityOnHand), bs.Where(b => b.ExpiryDate >= today && b.ExpiryDate <= soon).Sum(b => b.QuantityOnHand));
        }).ToList();
        var inventoryValue = warehouses.Sum(w => w.CostValue);

        // ---- customers
        var customers = await db.Customers.AsNoTracking().ToListAsync(ct);
        var trailing = lines.GroupBy(l => l.CustomerId).ToDictionary(g => g.Key, g => g.Sum(l => l.Line));
        var top = customers.Where(c => c.CustomerType != CustomerTypes.WalkIn && trailing.ContainsKey(c.Id)).OrderByDescending(c => trailing[c.Id]).Take(4)
            .Select(c => new CustomerHero(c.Id, c.Name, CustomerRanking.For(trailing[c.Id]), c.Balance, monthPoints.Select(m =>
            {
                var a = new DateOnly(m.Year, m.Month, 1); var ls = lines.Where(l => l.CustomerId == c.Id && l.InvoiceDate >= a && l.InvoiceDate < a.AddMonths(1)).ToList();
                var s = ls.Sum(l => l.Line); var g = ls.Sum(l => l.Line - l.Cost);
                return new CustomerMonth(m.Year, m.Month, s, g, s == 0 ? 0 : Math.Round(g / s * 100m, 1, MidpointRounding.ToEven));
            }).ToList())).ToList();

        // ---- products
        var names = await db.Products.AsNoTracking().Where(p => p.IsActive).ToDictionaryAsync(p => p.Id, ct);
        var onHand = batches.GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.Sum(b => b.QuantityOnHand));
        var since30 = today.AddDays(-30);
        var lastSale = lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Max(l => l.InvoiceDate));
        ProductMover Mover(int id, int q, decimal r)
        {
            var p = names[id]; var oh = onHand.GetValueOrDefault(id);
            return new ProductMover(id, p.Name, q, r, lastSale.TryGetValue(id, out var d) ? today.DayNumber - d.DayNumber : null, oh, oh * p.CostPrice);
        }
        var topProducts = lines.Where(l => l.InvoiceDate >= since30 && names.ContainsKey(l.ProductId)).GroupBy(l => l.ProductId)
            .Select(g => Mover(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Line))).OrderByDescending(m => m.Revenue).Take(6).ToList();
        var slow = names.Keys.Where(id => onHand.GetValueOrDefault(id) > 0 && !lines.Any(l => l.ProductId == id && l.InvoiceDate >= since30))
            .Select(id => Mover(id, 0, 0)).OrderByDescending(m => m.StockValue).Take(6).ToList();

        // ---- receivables
        var open = await OpenInvoicesAsync(ct);
        var receivables = customers.Sum(c => Math.Max(0, c.Balance));
        var overdue = open.Where(o => o.DueDate < today).Sum(o => o.Outstanding);

        var signals = Signals(today, revenue, gross, warehouses, slow, open, topProducts, customers, trailing, inventoryValue);
        return new Overview(today, revenue, gross, net, losses, collected, inventoryValue, customers.Count(c => c.CustomerType != CustomerTypes.WalkIn),
            0, receivables, overdue, dayPoints, monthPoints, warehouses, top, topProducts, slow, signals);
    }

    /// <summary>Open (unpaid or part-paid) invoices that have a due date. Payments are applied oldest-first, so each invoice's balance is its own.</summary>
    public async Task<List<DueItem>> OpenInvoicesAsync(CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var rows = await (from i in db.Invoices.AsNoTracking() join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id
                          where i.Status != PaymentStatuses.Voided && i.DueDate != null && i.TotalAmount > i.AmountPaid
                          select new { i.Id, i.InvoiceNumber, CustomerId = c.Id, c.Name, c.Phone, Owed = i.TotalAmount - i.AmountPaid, Due = i.DueDate!.Value }).ToListAsync(ct);
        return rows.Select(r => new DueItem(r.Id, r.InvoiceNumber, r.CustomerId, r.Name, r.Phone, r.Owed, r.Due, Math.Max(0, today.DayNumber - r.Due.DayNumber))).OrderBy(r => r.DueDate).ToList();
    }

    /// <summary>What falls due in one calendar month, plus everything already overdue.</summary>
    public async Task<Calendar> CalendarAsync(int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100) throw new Common.BusinessRuleException("Choose a valid month.");
        var a = new DateOnly(year, month, 1); var b = a.AddMonths(1); var today = clock.BusinessToday;
        var open = await OpenInvoicesAsync(ct);
        return new Calendar(year, month, open.Where(o => o.DueDate >= a && o.DueDate < b).ToList(), open.Where(o => o.DueDate < today).OrderByDescending(o => o.Outstanding).Take(50).ToList());
    }

    private static List<Signal> Signals(DateOnly today, Kpi revenue, Kpi gross, IReadOnlyList<WarehouseStock> whs, IReadOnlyList<ProductMover> slow, IReadOnlyList<DueItem> open,
        IReadOnlyList<ProductMover> topProducts, IReadOnlyList<Domain.Entities.Customer> customers, Dictionary<int, decimal> trailing, decimal inventoryValue)
    {
        var s = new List<Signal>();
        var overdue = open.Where(o => o.DueDate < today).ToList();
        if (overdue.Count > 0)
            s.Add(new Signal("overdue", overdue.Any(o => o.DaysOverdue > 30) ? "high" : "medium", $"{overdue.Count} overdue invoice(s)",
                $"{overdue.Sum(o => o.Outstanding):N2} is past its due date; the oldest is {overdue.Max(o => o.DaysOverdue)} days late.", overdue.Sum(o => o.Outstanding)));
        var soonDue = open.Where(o => o.DueDate >= today && o.DueDate <= today.AddDays(7)).ToList();
        if (soonDue.Count > 0) s.Add(new Signal("due-soon", "low", $"{soonDue.Count} payment(s) due in the next 7 days", $"{soonDue.Sum(o => o.Outstanding):N2} expected.", soonDue.Sum(o => o.Outstanding)));
        var expired = whs.Sum(w => w.ExpiredUnits); var expiring = whs.Sum(w => w.ExpiringSoonUnits);
        if (expired > 0) s.Add(new Signal("expired", "high", $"{expired:N0} unit(s) already expired", "Expired stock cannot be sold; write it off or return it to the supplier.", null));
        if (expiring > 0) s.Add(new Signal("expiring", "medium", $"{expiring:N0} unit(s) expire within 30 days", "Discount or push these first to avoid a write-off.", null));
        var deadValue = slow.Sum(m => m.StockValue);
        if (deadValue > 0 && inventoryValue > 0)
            s.Add(new Signal("slow-stock", deadValue / inventoryValue > .25m ? "high" : "medium", $"{slow.Count} product(s) with stock but no sales in 30 days",
                $"{deadValue:N2} at cost is tied up in them ({deadValue / inventoryValue:P0} of inventory).", deadValue));
        if (revenue.ChangePct is { } r && r <= -15) s.Add(new Signal("sales-drop", "high", $"Sales are down {-r:0.#}% on last month", "Compare against the same point last month before reading too much into it.", revenue.Current - revenue.Previous));
        if (gross.ChangePct is { } g && g <= -15) s.Add(new Signal("margin-drop", "medium", $"Gross profit is down {-g:0.#}% on last month", "Check for price overrides and supplier cost increases.", gross.Current - gross.Previous));
        foreach (var c in customers.Where(c => c.CreditLimit > 0 && c.Balance > c.CreditLimit).Take(5))
            s.Add(new Signal("over-limit", "medium", $"{c.Name} is over their credit limit", $"Owes {c.Balance:N2} against a limit of {c.CreditLimit:N2}.", c.Balance - c.CreditLimit));
        return s.OrderBy(x => x.Severity switch { "high" => 0, "medium" => 1, _ => 2 }).ToList();
    }
}
