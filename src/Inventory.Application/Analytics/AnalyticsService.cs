using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Analytics;

public sealed record ForecastRow(int ProductId, string Product, string Unit, int OnHand, double WeeklyDemand, string Method, string Confidence, string Summary, int? DaysOfCover, DateOnly? RunsOutOn, int SuggestedOrder);
public sealed record BasketRuleRow(IReadOnlyList<string> Antecedent, string Consequent, double Support, double Confidence, double Lift, int Baskets);
public sealed record UnusualRow(long MovementId, DateTime At, string Product, string Warehouse, string Kind, int Quantity, int Typical, double Score, string Reference);
public sealed record AdviceLine(string Kind, string Text);
public sealed record SaleLineIn(int ProductId, int Quantity);
public sealed record PurchaseLineIn(int ProductId, decimal UnitCost);
public sealed record SuggestedLine(int ProductId, string Product, string Unit, int Quantity, int OnHand, double WeeklyDemand, DateOnly? RunsOutOn, decimal UnitCost, string Basis);

/// <summary>
/// The desktop app's analytics on this company's own records: demand forecasts that report how far off they have been, products that sell together,
/// stock movements that don't look like the others, and the advice shown while a sale or purchase order is being written.
/// Silence is the default for advice: a prompt that fires on every line stops being read.
/// </summary>
public sealed class AnalyticsService(IBusinessDbContext db, IClock clock)
{
    public const int LeadTimeWeeks = 4, HistoryWeeks = 26;
    private const double LiftBar = 1.3, AnomalyBar = 0.62;

    // ---------------------------------------------------------------- demand
    private sealed record SaleLine(int ProductId, DateOnly Date, int Quantity);

    private async Task<List<SaleLine>> SalesAsync(int weeks, CancellationToken ct)
    {
        var cutoff = clock.BusinessToday.AddDays(-7 * weeks);
        return await (from it in db.InvoiceItems.AsNoTracking() join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                      where i.InvoiceDate >= cutoff && i.Status != PaymentStatuses.Voided && !i.IsSample select new SaleLine(it.ProductId, i.InvoiceDate, it.Quantity)).ToListAsync(ct);
    }

    /// <summary>Weekly units, oldest first. Weeks with no sales are zeros (not gaps); the series starts at the product's first sale so a new product isn't judged on weeks before it existed.</summary>
    private List<double> Weekly(IEnumerable<SaleLine> sales, int weeks)
    {
        var today = clock.BusinessToday; var start = today.AddDays(-7 * weeks + 1);
        var by = sales.GroupBy(s => (s.Date.DayNumber - start.DayNumber) / 7).ToDictionary(g => g.Key, g => (double)g.Sum(s => s.Quantity));
        if (by.Count == 0) return [];
        var first = by.Keys.Min(); var last = (today.DayNumber - start.DayNumber) / 7;
        return Enumerable.Range(first, last - first + 1).Select(w => by.GetValueOrDefault(w)).ToList();
    }

    public async Task<List<ForecastRow>> ForecastAsync(CancellationToken ct)
    {
        var sales = (await SalesAsync(HistoryWeeks, ct)).GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var onHand = await db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        var today = clock.BusinessToday;
        var rows = new List<ForecastRow>();
        foreach (var p in products.Where(p => sales.ContainsKey(p.Id)))
        {
            var series = Weekly(sales[p.Id], HistoryWeeks);
            var ranked = DemandModel.Evaluate(series);
            var perWeek = ranked.Count > 0 ? ranked[0].Predicted : series.Count > 0 ? series.TakeLast(4).Average() : 0;
            var summary = ranked.Count > 0 ? ranked[0].Summary : $"{series.Count} week(s) of history — too short to score a method, so this is the recent average";
            var oh = onHand.GetValueOrDefault(p.Id);
            int? cover = perWeek > 0 ? (int)Math.Floor(oh / (perWeek / 7.0)) : null;
            var need = Math.Max(0, (int)Math.Round(perWeek * LeadTimeWeeks) - oh);
            rows.Add(new ForecastRow(p.Id, p.Name, p.Unit, oh, Math.Round(perWeek, 1), ranked.Count > 0 ? ranked[0].Method : "Recent average", ranked.Count > 0 ? ranked[0].Confidence : "Unknown",
                summary, cover, cover is int d ? today.AddDays(d) : null, need));
        }
        return rows.OrderBy(r => r.RunsOutOn ?? DateOnly.MaxValue).ThenByDescending(r => r.SuggestedOrder).ToList();
    }

    // ---------------------------------------------------------------- market basket
    private async Task<(List<int[]> Baskets, Dictionary<int, string> Names)> BasketsAsync(int daysBack, CancellationToken ct)
    {
        var cutoff = clock.BusinessToday.AddDays(-daysBack);
        var lines = await (from it in db.InvoiceItems.AsNoTracking() join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                           where i.InvoiceDate >= cutoff && i.Status != PaymentStatuses.Voided && !i.IsSample select new { i.Id, it.ProductId }).ToListAsync(ct);
        var names = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return (lines.GroupBy(l => l.Id).Select(g => g.Select(l => l.ProductId).Distinct().OrderBy(x => x).ToArray()).ToList(), names);
    }

    private async Task<List<(MarketBasket.Rule Rule, Dictionary<int, string> Names)>> RulesAsync(CancellationToken ct)
    {
        var (baskets, names) = await BasketsAsync(365, ct);
        if (baskets.Count == 0) return [];
        return MarketBasket.RulesFrom(MarketBasket.Apriori(baskets, 0.02), baskets.Count, 0.3).Select(r => (r, names)).ToList();
    }

    public async Task<List<BasketRuleRow>> SoldTogetherAsync(CancellationToken ct)
    {
        var rules = await RulesAsync(ct);
        return rules.Take(30).Select(x => new BasketRuleRow(x.Rule.Antecedent.Select(a => x.Names.GetValueOrDefault(a, "Product " + a)).ToList(), x.Names.GetValueOrDefault(x.Rule.Consequent, "Product " + x.Rule.Consequent),
            Math.Round(x.Rule.Support, 3), Math.Round(x.Rule.Confidence, 2), Math.Round(x.Rule.Lift, 1), x.Rule.Baskets)).ToList();
    }

    // ---------------------------------------------------------------- unusual movements
    public async Task<List<UnusualRow>> UnusualMovementsAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-180); var recent = clock.UtcNow.AddDays(-90);
        var mv = await (from m in db.StockMovements.AsNoTracking() join p in db.Products.AsNoTracking() on m.ProductId equals p.Id join w in db.Warehouses.AsNoTracking() on m.WarehouseId equals w.Id
                        where m.MovementDate >= since && m.MovementType != MovementTypes.Adjust && m.ReferenceType != MovementReferences.Transfer
                        select new { m.Id, m.MovementDate, m.ProductId, Product = p.Name, Warehouse = w.Name, m.MovementType, m.Quantity, m.ReferenceType, m.ReferenceId }).ToListAsync(ct);
        var flagged = new List<UnusualRow>();
        foreach (var g in mv.GroupBy(x => (x.ProductId, x.MovementType)))
        {
            var list = g.ToList();
            if (list.Count < 12) continue;                                         // under a dozen there is no "usual" to be unusual against
            var qtys = list.Select(x => (double)x.Quantity).ToList();
            var typical = qtys.OrderBy(q => q).ElementAt(qtys.Count / 2);
            var model = Forecasting.TrainIsolationForest(list.Select(x => new[] { (double)x.Quantity }).ToList());
            foreach (var x in list.Where(x => x.MovementDate >= recent && x.Quantity > typical))    // only upward: fewer than usual is not a mistake
            {
                var score = model.Score([x.Quantity]);
                if (score >= AnomalyBar)
                    flagged.Add(new UnusualRow(x.Id, x.MovementDate, x.Product, x.Warehouse, x.MovementType == MovementTypes.Out ? "Stock out" : "Stock in", x.Quantity, (int)typical, Math.Round(score, 2), $"{x.ReferenceType} {x.ReferenceId}"));
            }
        }
        return flagged.OrderByDescending(f => f.Score).ThenByDescending(f => f.At).Take(40).ToList();
    }

    // ---------------------------------------------------------------- advice at the till
    public async Task<List<AdviceLine>> SaleAdviceAsync(int? customerId, IReadOnlyList<SaleLineIn> lines, CancellationToken ct)
    {
        var advice = new List<AdviceLine>();
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        if (ids.Count == 0) return advice;

        // Upsell: only products not already on the sale, strongest association first, and only when lift clears 1.3.
        var rules = await RulesAsync(ct);
        if (rules.Count > 0)
        {
            var names = rules[0].Names; var onSale = ids.Select(i => names.GetValueOrDefault(i, "")).ToHashSet();
            var hit = rules.FirstOrDefault(x => x.Rule.Lift >= LiftBar && !ids.Contains(x.Rule.Consequent) && x.Rule.Antecedent.All(ids.Contains));
            if (hit.Names is not null)
                advice.Add(new("upsell", $"Customers who buy {string.Join(" + ", hit.Rule.Antecedent.Select(a => names.GetValueOrDefault(a, "")))} usually take {names.GetValueOrDefault(hit.Rule.Consequent, "")} too — {hit.Rule.Confidence:P0} of the time, {hit.Rule.Lift:0.0}× above chance. Worth offering."));
        }

        var since = clock.BusinessToday.AddDays(-180); var six = clock.BusinessToday.AddDays(-7 * 6);
        var history = await (from it in db.InvoiceItems.AsNoTracking() join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                             where ids.Contains(it.ProductId) && i.InvoiceDate >= since && i.Status != PaymentStatuses.Voided select new { it.ProductId, it.Quantity, i.InvoiceDate }).ToListAsync(ct);
        var stock = await db.StockBatches.AsNoTracking().Where(b => ids.Contains(b.ProductId)).GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var productNames = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        foreach (var l in lines.GroupBy(x => x.ProductId).Select(g => new SaleLineIn(g.Key, g.Sum(x => x.Quantity))))
        {
            var mine = history.Where(h => h.ProductId == l.ProductId).ToList();
            var name = productNames.GetValueOrDefault(l.ProductId, "this product");
            if (mine.Count >= 12)
            {
                var qs = mine.Select(h => (double)h.Quantity).ToList(); var typical = qs.OrderBy(q => q).ElementAt(qs.Count / 2);
                if (l.Quantity > typical)
                {
                    var pts = qs.Select(q => new[] { q }).ToList(); pts.Add([l.Quantity]);   // the judged value goes into the sample, or an extreme point reads as ordinary
                    if (Forecasting.TrainIsolationForest(pts).Score([l.Quantity]) >= AnomalyBar)
                        advice.Add(new("quantity", $"{name}: {l.Quantity:N0} is well outside what usually goes out (typically about {typical:N0}). Check the quantity before saving."));
                }
            }
            var weekly = mine.Where(h => h.InvoiceDate >= six).Sum(h => h.Quantity) / 6.0;
            if (weekly > 0)
            {
                var left = Math.Max(0, stock.GetValueOrDefault(l.ProductId) - l.Quantity); var weeks = left / weekly;
                if (weeks < LeadTimeWeeks)
                    advice.Add(new("stockout", $"{name}: this leaves {left:N0} in stock — about {Math.Round(weeks * 7)} day(s) at the current rate, and a delivery takes around {LeadTimeWeeks} weeks. Worth reordering."));
            }
        }

        if (customerId is int cid)
        {
            var seg = await SegmentAsync(cid, ct);
            if (seg is not null) advice.Add(new("segment", seg));
        }
        return advice;
    }

    /// <summary>K-means over orders, average order and total spend; the label is ranked by spend so it means the same thing every time.</summary>
    private async Task<string?> SegmentAsync(int customerId, CancellationToken ct)
    {
        var rows = await (from c in db.Customers.AsNoTracking()
                          select new { c.Id, Orders = db.Invoices.Count(i => i.CustomerId == c.Id && i.Status != PaymentStatuses.Voided),
                                       Spend = db.Invoices.Where(i => i.CustomerId == c.Id && i.Status != PaymentStatuses.Voided).Sum(i => (decimal?)i.TotalAmount) ?? 0 }).ToListAsync(ct);
        if (rows.Count < 6) return null;                                              // three segments need meaningfully more than three customers
        var index = rows.FindIndex(r => r.Id == customerId); if (index < 0) return null;
        var feats = rows.Select(r => new[] { (double)r.Orders, r.Orders == 0 ? 0 : (double)r.Spend / r.Orders, (double)r.Spend }).ToList();
        var clusters = Learning.KMeans(Learning.Normalise(feats), 3);
        var mine = clusters.FirstOrDefault(c => c.Members.Contains(index)); if (mine is null) return null;
        var place = clusters.OrderByDescending(c => c.Centre[2]).ToList().IndexOf(mine);
        return $"{(place == 0 ? "A top customer" : place == 1 ? "A regular customer" : "An occasional customer")} — {rows[index].Orders} order(s) on record.";
    }

    // ---------------------------------------------------------------- advice on a purchase order
    /// <summary>Everything this supplier has sent before that won't survive the lead time, quantities from the demand forecast, most pressing first.</summary>
    public async Task<List<SuggestedLine>> SuggestOrderAsync(int supplierId, CancellationToken ct)
    {
        var past = await (from poi in db.PurchaseOrderItems.AsNoTracking() join po in db.PurchaseOrders.AsNoTracking() on poi.PurchaseOrderId equals po.Id
                          where po.SupplierId == supplierId select new { poi.ProductId, poi.UnitCost, po.Id }).ToListAsync(ct);
        var last = past.GroupBy(p => p.ProductId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First().UnitCost);
        if (last.Count == 0) return [];
        var forecast = (await ForecastAsync(ct)).ToDictionary(f => f.ProductId);
        var products = await db.Products.AsNoTracking().Where(p => last.Keys.Contains(p.Id) && p.IsActive).ToListAsync(ct);
        var list = new List<SuggestedLine>();
        foreach (var p in products)
        {
            if (!forecast.TryGetValue(p.Id, out var f) || f.WeeklyDemand <= 0) continue;           // nothing is selling; don't restock it
            if (f.SuggestedOrder <= 0) continue;                                                    // covered already
            list.Add(new SuggestedLine(p.Id, p.Name, p.Unit, f.SuggestedOrder, f.OnHand, f.WeeklyDemand, f.RunsOutOn, last[p.Id],
                $"{f.WeeklyDemand:0.#}/week forecast, {f.OnHand:N0} on hand, {LeadTimeWeeks}-week lead time. {f.Summary}"));
        }
        return list.OrderBy(s => s.RunsOutOn ?? DateOnly.MaxValue).ThenByDescending(s => s.Quantity).ToList();
    }

    public async Task<List<AdviceLine>> PurchaseAdviceAsync(int supplierId, IReadOnlyList<PurchaseLineIn> lines, CancellationToken ct)
    {
        var advice = new List<AdviceLine>();
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        if (ids.Count == 0) return advice;

        // A product that sells alongside one already on the order and isn't on it: arriving with one half of a pair means the pair can't be sold.
        var rules = await RulesAsync(ct);
        var hit = rules.FirstOrDefault(x => x.Rule.Lift >= LiftBar && !ids.Contains(x.Rule.Consequent) && x.Rule.Antecedent.All(ids.Contains));
        if (hit.Names is not null)
            advice.Add(new("partner", $"{string.Join(" + ", hit.Rule.Antecedent.Select(a => hit.Names.GetValueOrDefault(a, "")))} and {hit.Names.GetValueOrDefault(hit.Rule.Consequent, "")} sell together ({hit.Rule.Lift:0.0}× above chance). {hit.Names.GetValueOrDefault(hit.Rule.Consequent, "")} isn't on this order."));

        // Unit cost against what this supplier has charged before: catches a mis-keyed cost and a quiet price rise with the same test.
        var costs = await (from poi in db.PurchaseOrderItems.AsNoTracking() join po in db.PurchaseOrders.AsNoTracking() on poi.PurchaseOrderId equals po.Id
                           where po.SupplierId == supplierId && ids.Contains(poi.ProductId) select new { poi.ProductId, poi.UnitCost }).ToListAsync(ct);
        var names = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        foreach (var l in lines)
        {
            var mine = costs.Where(c => c.ProductId == l.ProductId).Select(c => (double)c.UnitCost).ToList();
            if (mine.Count < 8) continue;
            var typical = mine.OrderBy(c => c).ElementAt(mine.Count / 2);
            var pts = mine.Select(c => new[] { c }).ToList(); pts.Add([(double)l.UnitCost]);
            if (Forecasting.TrainIsolationForest(pts).Score([(double)l.UnitCost]) >= AnomalyBar)
                advice.Add(new("cost", $"{names.GetValueOrDefault(l.ProductId, "This product")}: ₦{l.UnitCost:N2} is well {((double)l.UnitCost > typical ? "higher" : "lower")} than this supplier's usual ₦{typical:N2}. Worth checking before ordering."));
        }
        return advice;
    }
}
