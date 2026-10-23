using System.Globalization;
using Inventory.Application.Abstractions;
using Inventory.Application.Analytics;
using Inventory.Application.Payments;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Campaigns;

public enum PrimeKind { None, Order, AskQuantity }

/// <summary>One message to one person, with every fact it may mention. <see cref="Prime"/> says what a "YES" reply should mean when they answer.</summary>
public sealed record Target(int? CustomerId, string Name, string? Phone, string? Email, string Language, string Kind, Dictionary<string, string> Facts,
    PrimeKind Prime = PrimeKind.None, List<(int ProductId, int Qty)>? Lines = null, int? ProductId = null);

public sealed record AdminDigest(string Subject, string Text, int Products);

/// <summary>
/// Decides WHO gets WHAT from the shop's own records, today. Everything here is a database query and arithmetic — including the "will it run out"
/// part (the same demand forecast the Insights screen uses) — so a message can only mention stock that exists and prices that are real.
/// Only customers with <see cref="MarketingStatuses.OptedIn"/> are ever considered, and nobody who was messaged within the campaign's cooldown.
/// </summary>
public sealed class CampaignPlanner(IBusinessDbContext db, IClock clock, StockAvailabilityService stock, AnalyticsService analytics, ICompanyContext company)
{
    private static string Money(decimal m) => ChatOrderText.Naira(m);
    private static string Day(DateOnly d) => d.ToString("d MMM", CultureInfo.InvariantCulture);

    public static string FirstName(string? full)
    {
        var first = (full ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length == 0 || first.All(char.IsDigit) ? "friend" : first;
    }

    public async Task<List<Target>> PlanAsync(Campaign c, CancellationToken ct)
    {
        var raw = c.Type switch
        {
            CampaignTypes.Reorder => await ReorderAsync(c, ct),
            CampaignTypes.UnpaidOrder => await UnpaidAsync(c, ct),
            CampaignTypes.Promo => await PromoAsync(c, ct),
            CampaignTypes.ChatDropOff => await DropOffAsync(c, ct),
            _ => [],
        };
        var since = clock.UtcNow.AddDays(-Math.Max(0, c.CooldownDays));
        // a failed attempt counts for a day too: a broken channel (Twilio refusing) must not turn every re-run into another try at the same person
        var day = clock.UtcNow.AddDays(-1);
        var recent = (await db.CampaignMessages.AsNoTracking().Where(m => m.CustomerId != null && ((m.Status == CampaignMessageStatuses.Sent && m.CreatedAt > since) || (m.CampaignId == c.Id && m.Status == CampaignMessageStatuses.Failed && m.CreatedAt > day)))
            .Select(m => m.CustomerId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        return raw.Where(t => t.CustomerId is null || !recent.Contains(t.CustomerId.Value))
            .GroupBy(t => t.CustomerId).Select(g => g.First()).Take(Math.Max(1, c.MaxRecipients)).ToList();
    }

    // ------------------------------------------------------------------ "you bought it N days ago; it's probably finished"

    private sealed record Purchase(int CustomerId, int ProductId, DateOnly Date, int Quantity);

    private async Task<List<Target>> ReorderAsync(Campaign c, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var rows = await (from it in db.InvoiceItems.AsNoTracking()
                          join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                          join cu in db.Customers.AsNoTracking() on i.CustomerId equals cu.Id
                          where cu.MarketingStatus == MarketingStatuses.OptedIn && (cu.Phone != null || cu.Email != null)
                                && i.InvoiceDate >= today.AddDays(-365) && i.Status != PaymentStatuses.Voided && !i.IsSample
                          select new Purchase(cu.Id, it.ProductId, i.InvoiceDate, it.Quantity)).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var customerIds = rows.Select(r => r.CustomerId).Distinct().ToList();
        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.IsActive && !p.TracksSerial).ToDictionaryAsync(p => p.Id, ct);
        var available = await stock.AvailableAsync(productIds, null, ct);
        var cover = (await analytics.ForecastAsync(ct)).ToDictionary(f => f.ProductId, f => f);
        var midOrder = (await db.Quotations.AsNoTracking().Where(q => q.Status == QuotationStatuses.Open && q.CreatedAt > clock.UtcNow.AddDays(-3)).Select(q => q.CustomerId).ToListAsync(ct)).ToHashSet();

        var result = new List<(double Overdue, Target T)>();
        foreach (var byCustomer in rows.GroupBy(r => r.CustomerId))
        {
            if (midOrder.Contains(byCustomer.Key)) continue;      // they're already buying: don't nag
            (double Ratio, int ProductId, DateOnly Last, int LastQty, int Days)? best = null;
            foreach (var byProduct in byCustomer.GroupBy(r => r.ProductId))
            {
                if (!products.TryGetValue(byProduct.Key, out _) || available.GetValueOrDefault(byProduct.Key) <= 0) continue;   // never pitch what we can't sell today
                var dates = byProduct.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
                var last = dates[^1];
                var interval = RepurchaseInterval(dates, c.FallbackRepurchaseDays);
                var days = today.DayNumber - last.DayNumber;
                if (days < interval * 0.9 || days > Math.Max(interval * 3, 90)) continue;
                var ratio = days / (double)interval;
                if (best is null || ratio > best.Value.Ratio)
                    best = (ratio, byProduct.Key, last, byProduct.Where(r => r.Date == last).Sum(r => r.Quantity), days);
            }
            if (best is not { } b) continue;

            var cu = customers[byCustomer.Key]; var p = products[b.ProductId]; var avail = available[b.ProductId];
            var urgent = avail <= p.ReorderLevel || (cover.TryGetValue(b.ProductId, out var f) && f.DaysOfCover is int d && d <= c.ThresholdDays);
            var qty = Math.Max(1, Math.Min(b.LastQty, avail));
            var facts = new Dictionary<string, string>
            {
                ["name"] = FirstName(cu.Name), ["product"] = p.Name, ["qty"] = b.LastQty.ToString(CultureInfo.InvariantCulture), ["days"] = b.Days.ToString(CultureInfo.InvariantCulture),
                ["date"] = Day(b.Last), ["stock"] = avail.ToString(CultureInfo.InvariantCulture), ["price"] = Money(p.PriceFor(PriceTiers.Retailer)), ["company"] = company.LegalName,
            };
            if (cover.TryGetValue(b.ProductId, out var fr) && fr.RunsOutOn is DateOnly ro) facts["runout"] = Day(ro);
            result.Add((b.Ratio + (urgent ? 1 : 0), new Target(cu.Id, cu.Name, cu.Phone, cu.Email, cu.PreferredLanguage ?? ChatLanguages.English,
                urgent ? CampaignText.ReorderUrgent : CampaignText.Reorder, facts, PrimeKind.Order, [(b.ProductId, qty)])));
        }
        return result.OrderByDescending(r => r.Overdue).Select(r => r.T).ToList();       // most overdue (and most at-risk stock) first, so the cap cuts the least urgent
    }

    /// <summary>The customer's own rhythm for this product: the median gap between their purchase dates, kept within 1–13 weeks; with fewer than two purchases, the campaign's default.</summary>
    public static int RepurchaseInterval(IReadOnlyList<DateOnly> sortedDistinctDates, int fallbackDays)
    {
        if (sortedDistinctDates.Count < 2) return Math.Clamp(fallbackDays, 3, 120);
        var gaps = sortedDistinctDates.Zip(sortedDistinctDates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).OrderBy(x => x).ToList();
        var median = gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2;
        return Math.Clamp(median, 7, 91);
    }

    // ------------------------------------------------------------------ orders left unpaid, balances past due

    private async Task<List<Target>> UnpaidAsync(Campaign c, CancellationToken ct)
    {
        var now = clock.UtcNow; var today = clock.BusinessToday;
        var nudged = (await db.CampaignMessages.AsNoTracking().Where(m => m.CampaignId == c.Id && m.Status == CampaignMessageStatuses.Sent && m.CreatedAt > now.AddDays(-7) && m.CustomerId != null)
            .Select(m => m.CustomerId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var targets = new List<Target>();

        var quotes = await db.Quotations.AsNoTracking().Include(q => q.Items)
            .Where(q => q.Status == QuotationStatuses.Open && q.CreatedAt < now.AddHours(-3) && q.CreatedAt > now.AddDays(-14)).OrderBy(q => q.CreatedAt).ToListAsync(ct);
        var quoteIds = quotes.Select(q => q.Id).ToList();
        var parked = (await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == PaymentDocTypes.Quotation && l.Status != PaymentLinkStatuses.Pending && quoteIds.Contains(l.DocId)).Select(l => l.DocId).ToListAsync(ct)).ToHashSet();
        foreach (var q in quotes.Where(q => !parked.Contains(q.Id)))
        {
            var cu = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.CustomerId, ct);
            if (cu is null || cu.MarketingStatus != MarketingStatuses.OptedIn || nudged.Contains(cu.Id)) continue;
            var ids = q.Items.Select(i => i.ProductId).ToList();
            var avail = await stock.AvailableAsync(ids, q.Id, ct);
            if (q.Items.Any(i => avail.GetValueOrDefault(i.ProductId) < i.Quantity)) continue;       // only nudge towards an order we can still fill
            var names = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Name).Select(p => p.Name).ToListAsync(ct);
            var label = names.Count > 1 ? $"{names[0]} and {names.Count - 1} more" : names.FirstOrDefault() ?? "your order";
            targets.Add(new Target(cu.Id, cu.Name, cu.Phone, cu.Email, cu.PreferredLanguage ?? ChatLanguages.English, CampaignText.Unpaid, new()
            {
                ["name"] = FirstName(cu.Name), ["product"] = label, ["total"] = Money(q.TotalAmount), ["days"] = Math.Max(1, (int)(now - q.CreatedAt).TotalDays).ToString(CultureInfo.InvariantCulture), ["company"] = company.LegalName,
            }, PrimeKind.Order, q.Items.Select(i => (i.ProductId, i.Quantity)).ToList()));
            nudged.Add(cu.Id);
        }

        var late = await db.Invoices.AsNoTracking()
            .Where(i => i.Status != PaymentStatuses.Paid && i.Status != PaymentStatuses.Voided && !i.IsSample && i.DueDate != null && i.DueDate < today)
            .Select(i => new { i.CustomerId, i.DueDate, Owed = i.TotalAmount - i.AmountPaid }).ToListAsync(ct);
        var overdue = late.GroupBy(i => i.CustomerId).Select(g => new { CustomerId = g.Key, Oldest = g.Min(i => i.DueDate), Owed = g.Sum(i => i.Owed) }).ToList();
        foreach (var o in overdue.Where(o => o.Owed > 0 && o.Oldest != null))
        {
            var cu = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == o.CustomerId, ct);
            if (cu is null || cu.MarketingStatus != MarketingStatuses.OptedIn || nudged.Contains(cu.Id)) continue;
            targets.Add(new Target(cu.Id, cu.Name, cu.Phone, cu.Email, cu.PreferredLanguage ?? ChatLanguages.English, CampaignText.Debt, new()
            {
                ["name"] = FirstName(cu.Name), ["total"] = Money(o.Owed), ["date"] = Day(o.Oldest!.Value), ["company"] = company.LegalName,
            }));
        }
        return targets;
    }

    // ------------------------------------------------------------------ picked products in the chat, left before ordering

    private async Task<List<Target>> DropOffAsync(Campaign c, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var since = now.AddDays(-Math.Max(1, c.ThresholdDays));
        var quiet = now.AddHours(-2);                                    // still mid-conversation: leave them be
        var events = await db.ChatEvents.AsNoTracking().Where(e => e.At >= since && e.ConversationId != null
                && (e.Kind == ChatEventKinds.Selected || e.Kind == ChatEventKinds.Order || e.Kind == ChatEventKinds.Sale))
            .Select(e => new { ConversationId = e.ConversationId!.Value, e.Kind, e.At, e.Reference }).ToListAsync(ct);
        var convIds = events.Select(e => e.ConversationId).Distinct().ToList();
        var convs = await db.ChatConversations.AsNoTracking().Where(x => convIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var sent = await db.CampaignMessages.AsNoTracking().Where(m => m.CampaignId == c.Id && m.Status == CampaignMessageStatuses.Sent && m.CreatedAt >= since && m.CustomerId != null)
            .Select(m => new { CustomerId = m.CustomerId!.Value, m.CreatedAt }).ToListAsync(ct);

        var picks = new List<(int CustomerId, DateTime At, List<(int ProductId, int Qty)> Lines)>();
        foreach (var g in events.GroupBy(e => e.ConversationId))
        {
            // Only what was picked AFTER the last order or sale in this chat: earlier picks were bought (or are the unpaid-order campaign's job).
            var lastBuy = g.Where(e => e.Kind != ChatEventKinds.Selected).Select(e => (DateTime?)e.At).Max();
            var selected = g.Where(e => e.Kind == ChatEventKinds.Selected && (lastBuy is null || e.At > lastBuy)).ToList();
            if (selected.Count == 0 || selected.Max(e => e.At) > quiet || !convs.TryGetValue(g.Key, out var conv)) continue;
            var lines = selected.Select(e => (e.Reference ?? "").Split(':'))
                .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
                .Select(p => (ProductId: int.Parse(p[0], CultureInfo.InvariantCulture), Qty: int.Parse(p[1], CultureInfo.InvariantCulture)))
                .GroupBy(l => l.ProductId).Select(x => (x.Key, x.Last().Qty)).Where(l => l.Qty > 0).ToList();
            var customerId = conv.CustomerId;
            if (customerId is null && conv.Channel == ChatChannels.WhatsApp && CustomerLookupService.Normalize(conv.ExternalId) is { } phone)
                customerId = await db.Customers.AsNoTracking().Where(x => x.Phone == phone).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
            if (customerId is int id && lines.Count > 0) picks.Add((id, selected.Max(e => e.At), lines));
        }

        var targets = new List<Target>();
        foreach (var p in picks.OrderByDescending(p => p.At).GroupBy(p => p.CustomerId).Select(g => g.First()))
        {
            if (sent.Any(s => s.CustomerId == p.CustomerId && s.CreatedAt > p.At)) continue;      // reminded once about this visit already
            var cu = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.CustomerId, ct);
            if (cu is null || cu.MarketingStatus != MarketingStatuses.OptedIn || (cu.Phone is null && cu.Email is null)) continue;
            var ids = p.Lines.Select(l => l.ProductId).ToList();
            var avail = await stock.AvailableAsync(ids, null, ct);
            if (p.Lines.Any(l => avail.GetValueOrDefault(l.ProductId) < l.Qty)) continue;          // only remind them of an order we can fill today
            var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id, ct);
            if (products.Count != ids.Count) continue;
            var total = p.Lines.Sum(l => products[l.ProductId].PriceFor(PriceTiers.Retailer) * l.Qty);
            var first = products[p.Lines[0].ProductId].Name;
            var label = p.Lines.Count > 1 ? $"{p.Lines[0].Qty} × {first} and {p.Lines.Count - 1} more" : $"{p.Lines[0].Qty} × {first}";
            targets.Add(new Target(cu.Id, cu.Name, cu.Phone, cu.Email, cu.PreferredLanguage ?? ChatLanguages.English, CampaignText.DropOff, new()
            {
                ["name"] = FirstName(cu.Name), ["product"] = label, ["total"] = Money(total), ["company"] = company.LegalName,
                ["days"] = Math.Max(1, (int)Math.Ceiling((now - p.At).TotalDays)).ToString(CultureInfo.InvariantCulture),
            }, PrimeKind.Order, p.Lines));
        }
        return targets;
    }

    // ------------------------------------------------------------------ an offer matched to what they bought before

    private async Task<List<Target>> PromoAsync(Campaign c, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var all = await stock.CatalogueAsync(null, ct);
        var inStock = all.Where(p => p.Available >= 5).ToList();
        if (inStock.Count == 0) return [];
        var cover = (await analytics.ForecastAsync(ct)).ToDictionary(f => f.ProductId, f => f);
        var catOf = await db.Products.AsNoTracking().Where(p => p.IsActive).ToDictionaryAsync(p => p.Id, p => p.CategoryId, ct);
        // Worth moving: plenty of cover (over six weeks) or no recent demand. If nothing qualifies, the fastest sellers.
        var worth = inStock.Where(p => !cover.TryGetValue(p.Id, out var f) || f.DaysOfCover is null || f.DaysOfCover > 45).ToList();
        if (worth.Count == 0) worth = inStock.OrderByDescending(p => cover.TryGetValue(p.Id, out var f) ? f.WeeklyDemand : 0).Take(3).ToList();

        var rows = await (from it in db.InvoiceItems.AsNoTracking()
                          join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                          join cu in db.Customers.AsNoTracking() on i.CustomerId equals cu.Id
                          where cu.MarketingStatus == MarketingStatuses.OptedIn && (cu.Phone != null || cu.Email != null)
                                && i.InvoiceDate >= today.AddDays(-365) && i.Status != PaymentStatuses.Voided && !i.IsSample
                          select new Purchase(cu.Id, it.ProductId, i.InvoiceDate, it.Quantity)).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var promoCustomerIds = rows.Select(r => r.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(x => promoCustomerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var names = all.ToDictionary(p => p.Id, p => p.Name);
        var result = new List<Target>();
        var perProduct = new Dictionary<int, int>();
        foreach (var g in rows.GroupBy(r => r.CustomerId).OrderByDescending(g => g.Max(r => r.Date)))
        {
            var lastBought = g.OrderByDescending(r => r.Date).First();
            if (!names.TryGetValue(lastBought.ProductId, out var lastName) || !catOf.TryGetValue(lastBought.ProductId, out var cat)) continue;
            var recentIds = g.Where(r => r.Date >= today.AddDays(-60)).Select(r => r.ProductId).ToHashSet();
            var pick = worth.Where(p => p.Id != lastBought.ProductId && !recentIds.Contains(p.Id) && catOf.GetValueOrDefault(p.Id) == cat)
                .OrderBy(p => perProduct.GetValueOrDefault(p.Id)).FirstOrDefault();       // spread the offers over the products worth moving
            if (pick is null) continue;
            perProduct[pick.Id] = perProduct.GetValueOrDefault(pick.Id) + 1;
            var cu = customers[g.Key];
            result.Add(new Target(cu.Id, cu.Name, cu.Phone, cu.Email, cu.PreferredLanguage ?? ChatLanguages.English, CampaignText.Promo, new()
            {
                ["name"] = FirstName(cu.Name), ["product"] = pick.Name, ["price"] = Money(pick.Price), ["stock"] = pick.Available.ToString(CultureInfo.InvariantCulture),
                ["last"] = lastName, ["company"] = company.LegalName,
            }, PrimeKind.AskQuantity, null, pick.Id));
        }
        return result;
    }

    // ------------------------------------------------------------------ the admin's restock digest (from today's inventory)

    public async Task<AdminDigest?> AdminDigestAsync(Campaign c, string? adminName, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        // a product nobody buys is not "running out", whatever its stock: only items with real demand are listed
        var risky = (await analytics.ForecastAsync(ct)).Where(f => f.WeeklyDemand > 0 && (f.OnHand <= 0 || (f.DaysOfCover is int d && d <= c.ThresholdDays))).OrderBy(f => f.DaysOfCover ?? 0).Take(10).ToList();
        if (risky.Count == 0) return null;                               // silence is the default: no digest on a day nothing is at risk

        var ids = risky.Select(r => r.ProductId).ToList();
        var topBuyers = await (from it in db.InvoiceItems.AsNoTracking()
                               join i in db.Invoices.AsNoTracking() on it.InvoiceId equals i.Id
                               join cu in db.Customers.AsNoTracking() on i.CustomerId equals cu.Id
                               where ids.Contains(it.ProductId) && i.InvoiceDate >= today.AddDays(-90) && i.Status != PaymentStatuses.Voided && !i.IsSample
                               group it by new { it.ProductId, cu.Name } into g
                               select new { g.Key.ProductId, g.Key.Name, Qty = g.Sum(x => x.Quantity) }).ToListAsync(ct);
        var best = topBuyers.GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Qty).First());

        var lines = risky.Select(r =>
        {
            var when = r.OnHand <= 0 ? "out now" : r.DaysOfCover is int d && d <= 0 ? "out today" : r.RunsOutOn is DateOnly ro ? $"out around {Day(ro)}" : "running low";
            var buyer = best.TryGetValue(r.ProductId, out var b) ? $" {b.Name} is your biggest buyer ({b.Qty} in 90 days)." : "";
            return $"• {r.Product}: {r.OnHand} {r.Unit} left, about {(r.WeeklyDemand / 7.0):0.#} a day, {when}. Suggested order: {r.SuggestedOrder}.{buyer}";
        });
        var greeting = string.IsNullOrWhiteSpace(adminName) ? "Good morning!" : $"Good morning {adminName.Trim().Split(' ')[0]}!";
        var text = $"{greeting} Stock check for {company.LegalName} from today's inventory:\n{string.Join('\n', lines)}\nSuggested orders cover a {AnalyticsService.LeadTimeWeeks}-week lead time. Raise a purchase order in the app when you're ready.";
        return new AdminDigest($"{company.LegalName}: {risky.Count} product(s) about to run out", text, risky.Count);
    }
}
