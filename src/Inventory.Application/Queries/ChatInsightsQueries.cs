using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Queries;

public sealed record ChatCounts(int Started, int Orders, int Quotes, int Waybills, int Sales, int Handoffs);
public sealed record ChatDay(DateOnly Date, int Requests, int Sales);
/// <summary>What the sales assistant brought in: people who started a chat, what they asked for, and how many ended in a paid sale.</summary>
public sealed record ChatSummary(int Days, ChatCounts Total, ChatCounts WhatsApp, ChatCounts Website, decimal SalesValue, decimal ConversionPct, IReadOnlyList<ChatDay> ByDay);

public sealed class ChatInsightsQueries(IBusinessDbContext db, IClock clock)
{
    private static ChatCounts Count(IEnumerable<ChatEvent> e)
    {
        var list = e.ToList();
        int N(string kind) => list.Count(x => x.Kind == kind);
        return new ChatCounts(N(ChatEventKinds.Started), N(ChatEventKinds.Order), N(ChatEventKinds.Quote), N(ChatEventKinds.Waybill), N(ChatEventKinds.Sale), N(ChatEventKinds.Handoff));
    }

    public async Task<ChatSummary> GetAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var today = clock.BusinessToday;
        var since = today.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue).AddHours(-1);      // Lagos midnight, stored as UTC
        var events = await db.ChatEvents.AsNoTracking().Where(e => e.At >= since).ToListAsync(ct);
        var total = Count(events);
        var requests = total.Orders + total.Quotes + total.Waybills;
        var byDay = Enumerable.Range(0, days).Select(i => today.AddDays(-(days - 1) + i)).Select(d =>
        {
            var day = events.Where(e => DateOnly.FromDateTime(e.At.AddHours(1)) == d).ToList();
            return new ChatDay(d, day.Count(e => e.Kind is ChatEventKinds.Order or ChatEventKinds.Quote or ChatEventKinds.Waybill), day.Count(e => e.Kind == ChatEventKinds.Sale));
        }).ToList();
        return new ChatSummary(days, total, Count(events.Where(e => e.Channel == ChatChannels.WhatsApp)), Count(events.Where(e => e.Channel != ChatChannels.WhatsApp)),
            events.Where(e => e.Kind == ChatEventKinds.Sale).Sum(e => e.Amount ?? 0), total.Started == 0 ? 0 : Math.Round(100m * total.Sales / total.Started, 1), byDay);
    }
}
