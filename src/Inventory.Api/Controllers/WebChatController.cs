using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Api.Controllers;

/// <param name="TimeZone">The browser's time zone ("Africa/Lagos"): where the visitor is, roughly, for the campaign insights. Optional.</param>
public sealed record WebChatRequest(string SessionId, string Message, string? TimeZone = null);
/// <param name="Reply">All of the bot's messages for this turn joined by blank lines (older widgets read only this).</param>
/// <param name="Replies">The same messages one by one, so the page can show them as separate bubbles.</param>
/// <param name="AwaitingPayment">True while a payment account has been given and not yet paid: the page then checks <c>updates</c> for the confirmation.</param>
/// <param name="At">Server time of this reply; pass it back to <c>updates</c> as <c>since</c>.</param>
public sealed record WebChatResponse(string Reply, string? CheckoutUrl, string? QuotationNumber, IReadOnlyList<string>? Replies = null, bool AwaitingPayment = false, DateTime? At = null);
public sealed record WebChatUpdates(IReadOnlyList<string> Messages, bool AwaitingPayment, DateTime At);
/// <summary>One product on the website's shop: what it is, today's retail price, and whether stock is running low. Exact stock counts stay private.</summary>
public sealed record ShopItem(int Id, string Name, string Unit, decimal Price, bool Few, bool HasImage, string? Category = null);
/// <summary>A store the website shows: who it is, the WhatsApp number its orders go to, and its public contact details.</summary>
public sealed record ShopStore(string Key, string Name, string LegalName, string? WhatsApp, string? Phone, string? Email, string? Address, string? Website);

/// <summary>
/// The chat on the landing page. Which engine answers is a setting (SalesAssistant:Engine): "ai" (the default, the AI sales assistant the site has used so far)
/// or "flow" (the word-based conversation also used on the Twilio WhatsApp number: AI only as a last resort, AlatPay bank account inside the chat, quotations,
/// delivery questions, Yoruba/Igbo/Hausa/Pidgin). There is no phone number on the web, so identity is a browser-generated session id. The company is fixed
/// to the landing page's own brand (WebChat:CompanyKey, default "chewypets").
/// </summary>
[ApiController, Route("api/webchat")]
public class WebChatController(IConfiguration config, Microsoft.Extensions.Options.IOptions<Inventory.Infrastructure.WhatsApp.TwilioOptions> twilio, Inventory.Infrastructure.Persistence.CompanyRegistry registry) : ControllerBase
{
    private string CompanyKey => config["WebChat:CompanyKey"] is { Length: > 0 } k ? k : "chewypets";

    [HttpGet("status"), AllowAnonymous]
    /// <summary>The landing page reads the WhatsApp number from here, so its links go to the number the sales assistant really answers on (the Twilio sandbox on a test machine).</summary>
    public object Status() => new { configured = true, whatsappNumber = WhatsAppDigits() };

    private string? WhatsAppDigits()
    {
        var from = config["WebChat:WhatsAppNumber"] is { Length: > 0 } set ? set
            : twilio.Value.Numbers.FirstOrDefault(n => n.CompanyKey.Equals(CompanyKey, StringComparison.OrdinalIgnoreCase))?.From;
        var digits = new string((from ?? "").Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits : null;
    }

    /// <summary>The businesses whose stock the website shows (Shop:Stores, default the landing brand then Candid Purrfect). Never anything a visitor names freely.</summary>
    private IReadOnlyList<string> StoreKeys =>
        (config["Shop:Stores"] is { Length: > 0 } s ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [CompanyKey, "candid"])
            .Where(k => registry.Find(k) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private string? Store(string? store) => string.IsNullOrWhiteSpace(store) ? CompanyKey : StoreKeys.FirstOrDefault(k => k.Equals(store, StringComparison.OrdinalIgnoreCase));

    /// <summary>The stores the website can show, with the WhatsApp number each one takes orders on and its public contact details (as printed on its invoices).</summary>
    [HttpGet("stores"), AllowAnonymous]
    public async Task<IReadOnlyList<ShopStore>> Stores([FromServices] IMemoryCache cache, CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return (await cache.GetOrCreateAsync("shop:stores", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var list = new List<ShopStore>();
            foreach (var key in StoreKeys)
            {
                var info = registry.Find(key)!;
                await using var db = registry.CreateFor(info);
                var p = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
                var phone = Digits(p?.Phone);
                // The landing brand's orders go to the number its sales assistant answers on; a sister store's to its own phone.
                var whatsapp = key.Equals(CompanyKey, StringComparison.OrdinalIgnoreCase) ? WhatsAppDigits() ?? phone : phone;
                list.Add(new ShopStore(key, info.DisplayName, p?.LegalName ?? info.LegalName, whatsapp, p?.Phone, p?.Email, p?.Address, info.Website));
            }
            return list;
        }))!;
    }

    private static string? Digits(string? phone)
    {
        var d = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (d.Length == 11 && d[0] == '0') d = "234" + d[1..];                 // 0703… -> 234703…
        return d.Length >= 10 ? d : null;
    }

    /// <summary>A store's shop: in-stock products with today's retail price and category, read from the same stock the chat sells from. Cached for a minute.</summary>
    [HttpGet("products"), AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<ShopItem>>> Products([FromQuery] string? store, [FromServices] IMemoryCache cache, CancellationToken ct)
    {
        if (Store(store) is not { } key) return NotFound();
        Response.Headers.CacheControl = "public, max-age=60";
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        return (await cache.GetOrCreateAsync("shop:" + key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var sp = HttpContext.RequestServices;
            var db = sp.GetRequiredService<IBusinessDbContext>();
            var cat = await sp.GetRequiredService<StockAvailabilityService>().CatalogueAsync(null, Inventory.Domain.PriceTiers.Retailer, ct);
            var withImage = (await db.ProductImages.AsNoTracking().Select(i => i.ProductId).ToListAsync(ct)).ToHashSet();
            var categories = await (from p in db.Products.AsNoTracking() join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id select new { p.Id, c.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return cat.Where(p => p.Available > 0 && p.Price > 0).OrderBy(p => p.Name)
                .Select(p => new ShopItem(p.Id, p.Name, p.Unit, p.Price, p.Available <= 5, withImage.Contains(p.Id), categories.GetValueOrDefault(p.Id))).ToList();
        }))!;
    }

    /// <summary>A product's photo for the shop (only for active products). Cached by browsers for a day.</summary>
    [HttpGet("products/{id:int}/image"), AllowAnonymous]
    public async Task<IActionResult> ProductImage(int id, [FromQuery] string? store, CancellationToken ct)
    {
        if (Store(store) is not { } key) return NotFound();
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        var db = HttpContext.RequestServices.GetRequiredService<IBusinessDbContext>();
        var img = await (from i in db.ProductImages.AsNoTracking() join p in db.Products.AsNoTracking() on i.ProductId equals p.Id
                         where i.ProductId == id && p.IsActive select new { i.Data, i.ContentType }).FirstOrDefaultAsync(ct);
        if (img is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(img.Data, img.ContentType);
    }
    [HttpPost("message"), AllowAnonymous, RequestSizeLimit(65536), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<WebChatResponse> Message(WebChatRequest r, CancellationToken ct)
    {
        var session = ValidSession(r.SessionId);
        HttpContext.Items[RequestCompany.OverrideItem] = CompanyKey;
        if (!SalesAssistantEngine.UsesFlow(config))
        {
            var assistant = HttpContext.RequestServices.GetRequiredService<Inventory.Application.SalesAssistant.SalesAssistantService>();
            var ai = await assistant.AskAsync(ChatChannels.WebChat, session, r.Message, ct);
            return new WebChatResponse(ai.Text, ai.CheckoutUrl, ai.QuotationNumber, [ai.Text], false, DateTime.UtcNow);
        }
        var flow = HttpContext.RequestServices.GetRequiredService<ChatFlowService>();
        var reply = await flow.HandleAsync(ChatChannels.WebChat, session, "", r.Message ?? "", ct, r.TimeZone);
        var state = await StateAsync(session, ct);
        return new WebChatResponse(string.Join("\n\n", reply.Messages), null, null, reply.Messages, state == ChatStates.AwaitingPayment, DateTime.UtcNow);
    }

    /// <summary>New bot messages since <paramref name="since"/> — how a payment confirmation reaches a customer who is still looking at the page.</summary>
    [HttpGet("updates"), AllowAnonymous, EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<WebChatUpdates> Updates([FromQuery] string sessionId, [FromQuery] DateTime since, CancellationToken ct)
    {
        var session = ValidSession(sessionId);
        HttpContext.Items[RequestCompany.OverrideItem] = CompanyKey;
        var db = HttpContext.RequestServices.GetRequiredService<IBusinessDbContext>();
        var now = DateTime.UtcNow;
        var sinceUtc = since.Kind == DateTimeKind.Local ? since.ToUniversalTime() : DateTime.SpecifyKind(since, DateTimeKind.Utc);
        var messages = await (from m in db.ChatMessages.AsNoTracking()
                              join c in db.ChatConversations.AsNoTracking() on m.ConversationId equals c.Id
                              where c.Channel == ChatChannels.WebChat && c.ExternalId == session && m.Role == ChatRoles.Assistant && m.CreatedAt > sinceUtc
                              orderby m.Id select m.Text).Take(20).ToListAsync(ct);
        return new WebChatUpdates(messages, await StateAsync(session, ct) == ChatStates.AwaitingPayment, now);
    }

    private static string ValidSession(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 100) throw new BusinessRuleException("Missing or invalid session id.");
        return s.Trim();
    }

    private async Task<string?> StateAsync(string session, CancellationToken ct)
    {
        var db = HttpContext.RequestServices.GetRequiredService<IBusinessDbContext>();
        return await db.ChatConversations.AsNoTracking().Where(c => c.Channel == ChatChannels.WebChat && c.ExternalId == session).Select(c => c.State).FirstOrDefaultAsync(ct);
    }
}
