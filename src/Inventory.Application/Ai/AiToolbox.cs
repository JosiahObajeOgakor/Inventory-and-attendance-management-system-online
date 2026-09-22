using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Dashboard;
using Inventory.Domain;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Ai;

public sealed record CustomerScore(int CustomerId, string Name, string Tier, decimal SpendLast12Months, int OrdersLast12Months, int? AvgDaysBetweenOrders, int? DaysSinceLastOrder,
    int OverdueInvoices, decimal Balance, decimal CreditLimit, decimal RebateRatePct, decimal RebateNotCollected, int Score, string Recommendation);

/// <summary>
/// The assistant's ONLY window onto data: read-only functions over this company's own records. The model never sees a database
/// connection, never writes, and cannot reach another company (the DbContext is bound to the signed-in company).
/// </summary>
public sealed class AiToolbox(IBusinessDbContext db, IClock clock, OverviewQueries overview, Inventory.Application.Analytics.AnalyticsService? analytics = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private const int MaxResultChars = 7000;

    public static readonly IReadOnlyList<ToolSpec> Specs =
    [
        new("business_overview", "This month vs last month: revenue, gross profit, net profit, losses, cash collected; inventory value; receivables; 12-month history; rule-based warning signals.", Obj()),
        new("sales_by_day", "Daily revenue, cash collected and gross profit for the last N days (7-90).", Obj(("days", "integer", "How many days back, 7 to 90"))),
        new("products_performance", "Best sellers and slow movers (stock with no sale in 30 days), with stock value tied up.", Obj()),
        new("stock_by_warehouse", "Units, cost value, retail value, low-stock batches and expired / soon-to-expire units in each warehouse.", Obj()),
        new("stock_forecast", "Per product: average daily demand, days of cover left, projected stock-out date and suggested reorder quantity. Lists the products that will run out soonest.", Obj()),
        new("expiring_stock", "The individual batches that are expired or expire within 30 days: product, SKU, batch number, warehouse, quantity, expiry date and cost value. Use this to name expired products and say where they are.", Obj()),
        new("demand_forecast", "Per product: forecast weekly demand, the method that has been most accurate on unseen weeks and its typical error, days of cover, the date it is expected to run out and a suggested order quantity. Soonest to run out first.", Obj()),
        new("sold_together", "Products that customers buy together (market basket rules) with confidence and lift.", Obj()),
        new("unusual_movements", "Recent stock movements whose quantity is unusually large for that product (possible keying slips).", Obj()),
        new("receivables", "Customers who owe money: each open invoice with its due date, amount owed and days overdue.", Obj()),
        new("customer_performance", "Score 0-100 per customer from spend, order frequency, recency and payment behaviour, with a recommendation on credit, bonus and rebate. Optionally filter by name.", Obj(("name", "string", "Part of the customer name (optional)"))),
        new("expenses_summary", "Expenses by category for the last N days (7-365).", Obj(("days", "integer", "How many days back"))),
        new("loss_risks", "What could turn into a loss: expiring or expired stock, dead stock, overdue money, products likely to stock out, and falling sales.", Obj()),
    ];

    private static string Obj(params (string Name, string Type, string Description)[] props) =>
        JsonSerializer.Serialize(new
        {
            type = "object",
            properties = props.ToDictionary(p => p.Name, p => (object)new { type = p.Type, description = p.Description }),
            additionalProperties = false,
        });

    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var a = doc.RootElement;
            int Int(string k, int d, int lo, int hi) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? Math.Clamp(n, lo, hi) : d;
            string? Str(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            object result = name switch
            {
                "business_overview" => await BusinessOverview(ct),
                "sales_by_day" => (await overview.GetAsync(Int("days", 30, 7, 90), ct)).Days,
                "products_performance" => await ProductsPerformance(ct),
                "stock_by_warehouse" => (await overview.GetAsync(7, ct)).Warehouses,
                "stock_forecast" => await StockForecast(ct),
                "expiring_stock" => await ExpiringStock(ct),
                "demand_forecast" => analytics is null ? new { error = "Not available." } : (await analytics.ForecastAsync(ct)).Take(15),
                "sold_together" => analytics is null ? new { error = "Not available." } : (await analytics.SoldTogetherAsync(ct)).Take(12),
                "unusual_movements" => analytics is null ? new { error = "Not available." } : (await analytics.UnusualMovementsAsync(ct)).Take(15),
                "receivables" => await Receivables(ct),
                "customer_performance" => await CustomerScores(Str("name"), ct),
                "expenses_summary" => await ExpensesSummary(Int("days", 30, 7, 365), ct),
                "loss_risks" => await LossRisks(ct),
                _ => new { error = "Unknown tool." },
            };
            var text = JsonSerializer.Serialize(result, Json);
            return text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "…[truncated]";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return JsonSerializer.Serialize(new { error = "Could not read that." }); }
    }

    private async Task<object> BusinessOverview(CancellationToken ct)
    {
        var o = await overview.GetAsync(7, ct);
        return new { o.AsOf, o.Revenue, o.GrossProfit, o.NetProfit, o.Losses, o.Collected, o.InventoryValue, o.Customers, o.ReceivablesTotal, o.OverdueTotal, o.Months, o.Signals,
            note = "Revenue is sales before VAT and before invoice-level discount. Losses = voided sales + stock written off at cost." };
    }

    private async Task<object> ProductsPerformance(CancellationToken ct)
    {
        var o = await overview.GetAsync(7, ct);
        return new { bestSellersLast30Days = o.TopProducts, slowMoversNoSaleIn30Days = o.SlowMovers };
    }

    private async Task<object> StockForecast(CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var since = clock.UtcNow.AddDays(-RestockAdvisor.DemandWindowDays);
        var sold = await db.StockMovements.AsNoTracking().Where(m => m.MovementType == MovementTypes.Out && m.ReferenceType == MovementReferences.Invoice && m.MovementDate >= since)
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, S = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.S, ct);
        var onHand = await db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        var rows = products.Select(p =>
        {
            var adv = RestockAdvisor.Advise(onHand.GetValueOrDefault(p.Id), p.ReorderLevel, sold.GetValueOrDefault(p.Id));
            return new
            {
                product = p.Name, onHand = adv.OnHand, avgDailyDemand = adv.AvgDailyDemand, daysOfCover = adv.DaysOfCover,
                stockOutOn = adv.DaysOfCover is { } d ? today.AddDays((int)Math.Floor(d)).ToString("yyyy-MM-dd") : null,
                suggestedOrderQty = adv.SuggestedOrderQty, urgency = adv.Urgency, estimatedDailyRevenueAtRisk = Math.Round(adv.AvgDailyDemand * p.PriceRetail, 2),
            };
        }).Where(r => r.urgency != "OK").OrderBy(r => r.daysOfCover ?? decimal.MaxValue).Take(15).ToList();
        return new { basis = $"Average daily demand over the last {RestockAdvisor.DemandWindowDays} days; lead time {RestockAdvisor.LeadTimeDays} days.", products = rows };
    }

    /// <summary>
    /// Customer score (0-100): spend 40, frequency 25, recency 15, payment behaviour 20. Thresholds are deliberately simple and visible
    /// so the owner can argue with them. Walk-in customers are excluded because they are not a relationship.
    /// </summary>
    public async Task<List<CustomerScore>> CustomerScores(string? name, CancellationToken ct)
    {
        var today = clock.BusinessToday; var from = today.AddMonths(-12);
        var custs = await db.Customers.AsNoTracking().Where(c => c.CustomerType != CustomerTypes.WalkIn).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(name)) custs = custs.Where(c => c.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var ids = custs.Select(c => c.Id).ToList();
        var invs = await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.CustomerId) && i.InvoiceDate >= from && i.Status != PaymentStatuses.Voided && !i.IsSample)
            .Select(i => new { i.CustomerId, i.InvoiceDate, i.TotalAmount, i.AmountPaid, i.DueDate }).ToListAsync(ct);
        var rebates = await db.RebateEntries.AsNoTracking().Where(r => r.Status == "Accrued" && ids.Contains(r.CustomerId)).GroupBy(r => r.CustomerId).Select(g => new { g.Key, S = g.Sum(r => r.Amount) }).ToDictionaryAsync(x => x.Key, x => x.S, ct);

        var list = new List<CustomerScore>();
        foreach (var c in custs)
        {
            var mine = invs.Where(i => i.CustomerId == c.Id).OrderBy(i => i.InvoiceDate).ToList();
            if (mine.Count == 0 && c.Balance <= 0) continue;
            var spend = mine.Sum(i => i.TotalAmount);
            var gaps = mine.Zip(mine.Skip(1), (x, y) => y.InvoiceDate.DayNumber - x.InvoiceDate.DayNumber).ToList();
            int? avgGap = gaps.Count > 0 ? (int)Math.Round(gaps.Average()) : null;
            int? since = mine.Count > 0 ? today.DayNumber - mine[^1].InvoiceDate.DayNumber : null;
            var overdue = mine.Count(i => i.DueDate < today && i.TotalAmount > i.AmountPaid);

            var sSpend = 40 * Math.Min(1m, spend / RebateAndRankingThresholds.Gold);
            var sFreq = 25 * Math.Min(1m, mine.Count / 12m);
            var sRecency = since is null ? 0 : since <= 14 ? 15 : since <= 30 ? 11 : since <= 60 ? 6 : 2;
            var sPay = overdue == 0 ? 20 : overdue == 1 ? 8 : 0;
            var score = (int)Math.Round(sSpend + sFreq + sRecency + sPay);

            var rec = overdue > 0 ? "Hold new credit until the overdue invoice(s) are settled; no bonus."
                : score >= 75 ? "Strong: eligible for a credit-limit increase and a rebate bonus."
                : score >= 55 ? "Good: keep the current terms; a small rebate bump if orders continue at this pace."
                : score >= 35 ? "Average: no change; encourage more frequent ordering."
                : "Low activity: consider a re-engagement offer rather than a rebate.";
            list.Add(new CustomerScore(c.Id, c.Name, CustomerRanking.For(spend), spend, mine.Count, avgGap, since, overdue, c.Balance, c.CreditLimit, c.RebateRatePct, rebates.GetValueOrDefault(c.Id), score, rec));
        }
        return list.OrderByDescending(s => s.Score).Take(25).ToList();
    }

    /// <summary>Who owes what, in full: each customer's running balance (the authoritative figure) with their open invoices and due dates. Totals are computed here so the model never adds numbers itself.</summary>
    private async Task<object> Receivables(CancellationToken ct)
    {
        var owing = await db.Customers.AsNoTracking().Where(c => c.Balance > 0).OrderByDescending(c => c.Balance).Take(40).ToListAsync(ct);
        var open = await overview.OpenInvoicesAsync(ct);
        var today = clock.BusinessToday;
        var customers = owing.Select(c => new
        {
            customer = c.Name, owes = c.Balance, phone = c.Phone, creditLimit = c.CreditLimit,
            openInvoices = open.Where(o => o.CustomerId == c.Id).Select(o => new { o.InvoiceNumber, o.Outstanding, dueDate = o.DueDate, o.DaysOverdue }).ToList(),
        }).ToList();
        return new
        {
            totalOwedByCustomers = owing.Sum(c => c.Balance),
            totalOverdue = open.Where(o => o.DueDate < today).Sum(o => o.Outstanding),
            totalNotYetDue = open.Where(o => o.DueDate >= today).Sum(o => o.Outstanding),
            customers,
            note = "owes is the customer's running balance; invoices without a due date appear only in owes.",
        };
    }


    /// <summary>Batch-level detail behind the expired / expiring warnings: which product, which batch, which warehouse, how many, when.</summary>
    private async Task<object> ExpiringStock(CancellationToken ct)
    {
        var today = clock.BusinessToday; var horizon = today.AddDays(30);
        var rows = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id join w in db.Warehouses.AsNoTracking() on b.WarehouseId equals w.Id
                          where b.QuantityOnHand > 0 && b.ExpiryDate != null && b.ExpiryDate <= horizon
                          orderby b.ExpiryDate
                          select new { p.Name, p.Sku, b.BatchNumber, Warehouse = w.Name, b.QuantityOnHand, b.ExpiryDate, Cost = b.QuantityOnHand * p.CostPrice }).Take(60).ToListAsync(ct);
        var items = rows.Select(r => new
        {
            product = r.Name, sku = r.Sku, batch = r.BatchNumber, warehouse = r.Warehouse, quantity = r.QuantityOnHand, expiryDate = r.ExpiryDate,
            status = r.ExpiryDate < today ? "expired" : "expires within 30 days", daysToExpiry = r.ExpiryDate!.Value.DayNumber - today.DayNumber, costValue = r.Cost,
        }).ToList();
        return new
        {
            expiredUnits = items.Where(i => i.status == "expired").Sum(i => i.quantity), expiredCostValue = items.Where(i => i.status == "expired").Sum(i => i.costValue),
            expiringSoonUnits = items.Where(i => i.status != "expired").Sum(i => i.quantity), batches = items,
        };
    }


    private async Task<object> ExpensesSummary(int days, CancellationToken ct)
    {
        var from = clock.BusinessToday.AddDays(-days);
        var rows = await db.Expenses.AsNoTracking().Where(e => e.ExpenseDate >= from).GroupBy(e => e.Category).Select(g => new { category = g.Key, total = g.Sum(e => e.Amount), count = g.Count() }).ToListAsync(ct);
        return new { days, total = rows.Sum(r => r.total), byCategory = rows.OrderByDescending(r => r.total) };
    }

    private async Task<object> LossRisks(CancellationToken ct)
    {
        var o = await overview.GetAsync(30, ct);
        var forecast = await StockForecast(ct);
        var expiredCost = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                                 where b.ExpiryDate < clock.BusinessToday && b.QuantityOnHand > 0 select (decimal?)(b.QuantityOnHand * p.CostPrice)).SumAsync(ct) ?? 0;
        return new { expiredStockCostValue = expiredCost, warehouses = o.Warehouses.Select(w => new { w.Name, w.ExpiredUnits, w.ExpiringSoonUnits, w.LowBatches }), deadStock = o.SlowMovers,
            overdueReceivables = o.OverdueTotal, signals = o.Signals, likelyStockOuts = forecast };
    }
}

/// <summary>Mirrors <see cref="CustomerRanking"/> so the score's spend component tops out at the Gold threshold.</summary>
internal static class RebateAndRankingThresholds { public const decimal Gold = 800_000m; }
