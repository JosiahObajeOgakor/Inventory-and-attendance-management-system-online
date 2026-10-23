using System.Globalization;
using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Campaigns;

public sealed record StockIdea(string Focus, string Title, string Why, string Message, bool ByAi, string Audience);

/// <summary>
/// Reads how stock is performing and drafts a broadcast to move it: best sellers that are in stock, slow movers sitting on the shelf, or batches close to
/// expiry. The products, quantities and prices come from the records; the AI (when switched on) only words the message around them, and its draft is
/// checked like every campaign message (no numbers, prices or links of its own). Without the AI a plain, vetted wording is used.
/// The marketer can edit the result, and it still needs an admin's approval to go out.
/// </summary>
public sealed class CampaignIdeas(IBusinessDbContext db, IClock clock, ICompanyContext company, IChatModel model, ILogger<CampaignIdeas> log)
{
    public const string BestSellers = "Best sellers in stock", SlowMovers = "Slow-moving stock", Expiring = "Stock expiring soon";
    public static readonly string[] Focuses = [BestSellers, SlowMovers, Expiring];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Item(int Id, string Name, string Unit, decimal Price, int OnHand, int Sold30, DateOnly? Expires);

    public async Task<StockIdea> DraftAsync(string? focus, CancellationToken ct)
    {
        var items = await PerformanceAsync(ct);
        if (items.Count == 0) throw new Common.BusinessRuleException("There is no priced product in stock to promote yet.");
        var today = clock.BusinessToday;
        // the chosen focus, or the one with the most to gain: expiring stock first, then slow movers, then best sellers
        var picks = new Dictionary<string, List<Item>>
        {
            [Expiring] = items.Where(i => i.Expires is { } e && e <= today.AddDays(45)).OrderBy(i => i.Expires).Take(3).ToList(),
            [SlowMovers] = items.Where(i => i.OnHand >= 10 && (i.Sold30 == 0 || i.OnHand / Math.Max(1m, i.Sold30) > 2)).OrderByDescending(i => i.OnHand / Math.Max(1m, i.Sold30)).Take(3).ToList(),
            [BestSellers] = items.Where(i => i.Sold30 > 0 && i.OnHand >= 5).OrderByDescending(i => i.Sold30).Take(3).ToList(),
        };
        var chosen = focus is not null && picks.ContainsKey(focus) && picks[focus].Count > 0 ? focus : Focuses.FirstOrDefault(f => picks[f].Count > 0) ?? BestSellers;
        var list = picks[chosen].Count > 0 ? picks[chosen] : items.OrderByDescending(i => i.OnHand).Take(3).ToList();

        var why = string.Join("; ", list.Select(i => $"{i.Name}: {i.OnHand} {i.Unit.ToLowerInvariant()} in stock, {i.Sold30} sold in 30 days" + (i.Expires is { } e ? $", earliest expiry {e:d MMM}" : "")));
        var names = string.Join(", ", list.Select(i => i.Name));
        var priced = string.Join("\n", list.Select(i => $"• {i.Name}: {Naira(i.Price)} per {i.Unit.ToLowerInvariant()}"));
        var opening = await WordingAsync(chosen, names, ct);
        var message = $"{opening.Text}\n{priced}\nReply on WhatsApp to order. — {company.LegalName}";
        var audience = chosen == BestSellers ? "All opted-in customers" : "Opted-in customers who haven't bought in 90 days";
        if (chosen == Expiring) audience = "All opted-in customers";
        return new StockIdea(chosen, $"{chosen}: {names}", why, message, opening.ByAi, audience);
    }

    private async Task<List<Item>> PerformanceAsync(CancellationToken ct)
    {
        var today = clock.BusinessToday; var since = today.AddDays(-30);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive && p.PriceRetail > 0).Select(p => new { p.Id, p.Name, p.Unit, p.PriceRetail }).ToListAsync(ct);
        var stock = await db.StockBatches.AsNoTracking().Where(b => b.QuantityOnHand > 0 && (b.ExpiryDate == null || b.ExpiryDate >= today))
            .GroupBy(b => b.ProductId).Select(g => new { g.Key, Qty = g.Sum(b => b.QuantityOnHand), Expires = g.Min(b => b.ExpiryDate) }).ToDictionaryAsync(x => x.Key, ct);
        var sold = await (from i in db.InvoiceItems.AsNoTracking()
                          join inv in db.Invoices.AsNoTracking() on i.InvoiceId equals inv.Id
                          where inv.InvoiceDate >= since && inv.VoidedAt == null && !inv.IsSample
                          group i by i.ProductId into g
                          select new { g.Key, Qty = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Qty, ct);
        return products.Where(p => stock.ContainsKey(p.Id))
            .Select(p => new Item(p.Id, p.Name, p.Unit, p.PriceRetail, stock[p.Id].Qty, sold.GetValueOrDefault(p.Id), stock[p.Id].Expires)).ToList();
    }

    /// <summary>One opening line around the products, with {name} for the customer. AI-written when allowed and safe; otherwise the plain line.</summary>
    private async Task<(string Text, bool ByAi)> WordingAsync(string focus, string products, CancellationToken ct)
    {
        var plain = focus switch
        {
            Expiring => $"Hello {{name}}, we're clearing some stock early at our best prices: {products}. Here's what's available:",
            SlowMovers => $"Hello {{name}}, we have plenty of {products} ready for you right now. Here's today's price:",
            _ => $"Hello {{name}}, our customers' favourites are back on the shelf: {products}. Here's today's price:",
        };
        if (!model.IsConfigured) return (plain, false);
        try
        {
            var system = $$"""
                You write one short opening line for a WhatsApp offer from a Nigerian pet-feed shop. Warm, human, specific, never pushy.
                Angle: {{focus}}. The products are: {{products}}. The prices are listed below your line by the system.
                Rules: under 35 words, one line, include {name} for the customer's first name, mention the products by name, NO digits, prices, currency, links or hashtags, no invented discounts or delivery promises.
                Reply with ONLY JSON: {"line":"..."}
                """;
            var reply = await model.CompleteAsync([new("system", system), new("user", "Write the line.")], [], 200, ct);
            using var doc = JsonDocument.Parse(ExtractJson(reply.Content));
            var line = doc.RootElement.GetProperty("line").GetString()?.Trim() ?? "";
            // the same checks as every campaign message, except that product names may legitimately contain digits ("15KG")
            var check = line;
            foreach (var p in products.Split(", ")) check = check.Replace(p, "product", StringComparison.OrdinalIgnoreCase);
            return CampaignWriter.IsSafe(check) ? (line, true) : (plain, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "AI campaign line failed; using the plain wording"); return (plain, false); }
    }

    private static string ExtractJson(string? s)
    {
        s ??= ""; var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : "{}";
    }

    private static string Naira(decimal v) => "₦" + v.ToString(v % 1 == 0 ? "N0" : "N2", Inv);
}
