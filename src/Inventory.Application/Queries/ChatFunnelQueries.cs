using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Queries;

/// <param name="Stage">One of <see cref="ChatEventKinds.Funnel"/>.</param>
/// <param name="WhatsApp">WhatsApp conversations that got at least this far.</param>
/// <param name="Website">Website-chat conversations that got at least this far.</param>
public sealed record FunnelStep(string Stage, int WhatsApp, int Website);
/// <param name="Stage">Where these conversations stopped: the furthest step they reached without buying.</param>
/// <param name="Reachable">Of those, how many we may message: a known customer with a number who agreed to reminders.</param>
/// <param name="Campaign">The campaign that follows these people up, if one does.</param>
public sealed record DropOff(string Stage, int WhatsApp, int Website, int Reachable, string? Campaign);
public sealed record Slice(string Name, int Count);
/// <summary>The public website: visits, baskets started, baskets sent to WhatsApp, and online checkouts opened (with their value).</summary>
public sealed record SiteStats(int Views, int BasketsStarted, int BasketsSent, int Checkouts, decimal CheckoutValue);
/// <summary>How the chat performs for marketing: who comes, from where, in what language, when, how far they get, and who can be followed up.</summary>
public sealed record ChatFunnel(int Days, int Conversations, int CustomerMessages, decimal MessagesPerConversation, int AiCalls, int Quotes, int QuotesRefused,
    int Handoffs, int Sales, decimal SalesValue, decimal ConversionPct, IReadOnlyList<FunnelStep> Funnel, IReadOnlyList<DropOff> DropOffs,
    IReadOnlyList<Slice> Languages, IReadOnlyList<Slice> Countries, IReadOnlyList<Slice> Networks, IReadOnlyList<Slice> WebRegions, IReadOnlyList<int> MessagesByHour,
    SiteStats? Website = null);

public sealed class ChatFunnelQueries(IBusinessDbContext db, IClock clock)
{
    public async Task<ChatFunnel> GetAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var since = clock.BusinessToday.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue).AddHours(-1);      // Lagos midnight, stored as UTC

        var convs = await db.ChatConversations.AsNoTracking().Where(c => c.LastMessageAt >= since)
            .Select(c => new { c.Id, c.Channel, c.Language, c.Country, c.Network, c.Region, c.AiCalls, c.CustomerId }).ToListAsync(ct);
        var ids = convs.Select(c => c.Id).ToList();
        var events = await db.ChatEvents.AsNoTracking().Where(e => e.At >= since).Select(e => new { e.Kind, e.ConversationId, e.Amount, e.Channel }).ToListAsync(ct);
        // (the website's own events carry no conversation, so they never enter the chat funnel below)
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.CreatedAt >= since && m.Role == ChatRoles.User).Select(m => m.CreatedAt).ToListAsync(ct);

        // Furthest step per conversation: everyone active in the period at least "started", then the furthest event they produced.
        var reached = convs.ToDictionary(c => c.Id, _ => 0);
        foreach (var e in events.Where(e => e.ConversationId is int id && reached.ContainsKey(id)))
        {
            var step = Array.IndexOf(ChatEventKinds.Funnel, e.Kind);
            if (step > reached[e.ConversationId!.Value]) reached[e.ConversationId.Value] = step;
        }
        bool Web(string channel) => channel != ChatChannels.WhatsApp;
        var funnel = ChatEventKinds.Funnel.Select((stage, i) => new FunnelStep(stage,
            convs.Count(c => !Web(c.Channel) && reached[c.Id] >= i), convs.Count(c => Web(c.Channel) && reached[c.Id] >= i))).ToList();

        var customerIds = convs.Where(c => c.CustomerId != null).Select(c => c.CustomerId!.Value).Distinct().ToList();
        var reachable = (await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id) && c.MarketingStatus == MarketingStatuses.OptedIn && (c.Phone != null || c.Email != null))
            .Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var saleStep = ChatEventKinds.Funnel.Length - 1;
        var drops = ChatEventKinds.Funnel.Take(saleStep).Select((stage, i) =>
        {
            var here = convs.Where(c => reached[c.Id] == i).ToList();
            var campaign = stage switch
            {
                ChatEventKinds.Selected or ChatEventKinds.Summary => CampaignTypes.ChatDropOff,
                ChatEventKinds.Order => CampaignTypes.UnpaidOrder,
                _ => null,
            };
            return new DropOff(stage, here.Count(c => !Web(c.Channel)), here.Count(c => Web(c.Channel)),
                here.Count(c => c.CustomerId is int cid && reachable.Contains(cid)), campaign);
        }).ToList();

        static List<Slice> Top(IEnumerable<string?> values) => values.Select(v => string.IsNullOrWhiteSpace(v) ? "Unknown" : v!)
            .GroupBy(v => v).Select(g => new Slice(g.Key, g.Count())).OrderByDescending(s => s.Count).ThenBy(s => s.Name).Take(10).ToList();
        var byHour = new int[24];
        foreach (var at in messages) byHour[at.AddHours(1).Hour]++;                                  // Lagos time

        int N(string kind) => events.Count(e => e.Kind == kind);
        var sales = N(ChatEventKinds.Sale);
        return new ChatFunnel(days, convs.Count, messages.Count, convs.Count == 0 ? 0 : Math.Round((decimal)messages.Count / convs.Count, 1), convs.Sum(c => c.AiCalls),
            N(ChatEventKinds.Quote), N(ChatEventKinds.QuoteLimited), N(ChatEventKinds.Handoff), sales,
            events.Where(e => e.Kind == ChatEventKinds.Sale).Sum(e => e.Amount ?? 0), convs.Count == 0 ? 0 : Math.Round(100m * sales / convs.Count, 1),
            funnel, drops, Top(convs.Select(c => c.Language)), Top(convs.Where(c => !Web(c.Channel)).Select(c => c.Country)),
            Top(convs.Where(c => !Web(c.Channel) && c.Country == "Nigeria").Select(c => c.Network)), Top(convs.Where(c => Web(c.Channel)).Select(c => c.Region)), byHour,
            new SiteStats(N(Website.SiteEvents.View), N(Website.SiteEvents.BasketStart), N(Website.SiteEvents.BasketSent), N(Website.SiteEvents.Checkout),
                events.Where(e => e.Kind == Website.SiteEvents.Checkout).Sum(e => e.Amount ?? 0)));
    }
}
