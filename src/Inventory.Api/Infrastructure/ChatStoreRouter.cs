using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// One WhatsApp line sells for every store (Shop:Stores: the line's own business first, then its sister stores). For each message it picks the store
/// whose stock and chat should answer:
/// 1. the customer names a store ("Hi Candid Purrfect! I'd like…", as the website's sister-store basket writes);
/// 2. they are in the middle of an order with a store (a conversation not idle, active in the last day): they stay there;
/// 3. the items they typed are sold only by a sister store;
/// 4. otherwise the line's own business.
/// No AI and no customer-facing question: a store is never guessed from anything but the customer's own words and their open order.
/// </summary>
public sealed class ChatStoreRouter(CompanyRegistry registry, IConfiguration config, IMemoryCache cache)
{
    /// <summary>Words that name a store in a message (normalised, whole words). The line's own business needs none: it is the default.</summary>
    private static readonly Dictionary<string, string[]> StoreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["candid"] = ["candid", "purrfect", "candid purrfect", "candidpurrfect"],
    };

    public IReadOnlyList<string> StoresFor(string homeKey)
    {
        var list = config["Shop:Stores"] is { Length: > 0 } s ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [homeKey, "candid"];
        return new[] { homeKey }.Concat(list).Where(k => registry.Find(k) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string> RouteAsync(string homeKey, string channel, string externalId, string message, CancellationToken ct)
    {
        var stores = StoresFor(homeKey);
        if (stores.Count < 2) return homeKey;
        var norm = ChatLexicon.Normalize(message);

        // 1. named
        foreach (var key in stores)
            if (StoreWords.TryGetValue(key, out var words) && words.Any(w => ChatLexicon.Has(norm, w))) return key;

        // 2. an order in progress
        var since = DateTime.UtcNow.AddDays(-1);
        (string Key, DateTime At)? open = null;
        foreach (var key in stores)
        {
            await using var db = registry.CreateFor(registry.Find(key)!);
            var c = await db.ChatConversations.AsNoTracking().Where(x => x.Channel == channel && x.ExternalId == externalId && x.State != ChatStates.Idle && x.LastMessageAt > since)
                .Select(x => new { x.LastMessageAt }).FirstOrDefaultAsync(ct);
            if (c is not null && (open is null || c.LastMessageAt > open.Value.At)) open = (key, c.LastMessageAt);
        }
        if (open is { } o) return o.Key;

        // 3. what they asked for is only sold by a sister store
        var raws = ProductMatcher.Split(norm);
        if (raws.Count == 0) return homeKey;
        if (raws.Any(r => ProductMatcher.Match(r, Catalogue(homeKey)).Matches.Count > 0)) return homeKey;
        foreach (var key in stores.Skip(1))
            if (raws.Any(r => ProductMatcher.Match(r, Catalogue(key)).Matches.Count > 0)) return key;
        return homeKey;
    }

    /// <summary>Active product names per store (for matching only: no prices or stock), cached for two minutes.</summary>
    private List<CatalogItem> Catalogue(string key) => cache.GetOrCreate("router:" + key, e =>
    {
        e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
        using var db = registry.CreateFor(registry.Find(key)!);
        return db.Products.AsNoTracking().Where(p => p.IsActive).Select(p => new { p.Id, p.Name }).ToList()
            .Select(p => new CatalogItem(p.Id, p.Name, "", 0, 1)).ToList();
    })!;
}
