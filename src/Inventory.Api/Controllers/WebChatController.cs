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
public sealed record ShopStore(string Key, string Name, string LegalName, string? WhatsApp, string? Phone, string? Email, string? Address, string? Website,
    string? OpeningHours = null, string? ReplyPromise = null, IReadOnlyList<ShopZone>? Delivery = null, IReadOnlyList<Inventory.Application.Abstractions.SocialLink>? Socials = null,
    decimal VatRate = 0, bool PayOnline = false);
public sealed record ShopZone(string Name, decimal Fee, string? Eta);
/// <summary>A product's page on the website: what it is, who it is for, its analysis and feeding guide, and today's price.</summary>
public sealed record ShopProduct(int Id, string Name, string Unit, decimal Price, bool InStock, bool Few, bool HasImage, string? Category, string? Species, string? LifeStage,
    decimal? ProteinPct, decimal? FatPct, decimal? FiberPct, decimal? MoisturePct, string? NutritionSummary, string? Description, string? FeedingGuide);
public sealed record TrackRequest(string Event, string? Store = null, decimal? Amount = null);

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

    /// <summary>The stores the website can show, the WhatsApp number orders go to (the one line, for all of them) and each store's public contact details (as printed on its invoices).</summary>
    [HttpGet("stores"), AllowAnonymous]
    public async Task<IReadOnlyList<ShopStore>> Stores([FromServices] IMemoryCache cache, CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return (await cache.GetOrCreateAsync("shop:stores", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var list = new List<ShopStore>();
            string? mainPhone;
            await using (var home = registry.CreateFor(registry.Find(CompanyKey)!))
                mainPhone = Digits(await home.CompanyProfiles.AsNoTracking().Select(x => x.Phone).FirstOrDefaultAsync(ct));
            foreach (var key in StoreKeys)
            {
                var info = registry.Find(key)!;
                await using var db = registry.CreateFor(info);
                var p = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
                // One WhatsApp line sells for every store (ChatStoreRouter): every store's orders go to the number the sales assistant answers on.
                var whatsapp = WhatsAppDigits() ?? mainPhone;
                var delivery = await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive).OrderBy(z => z.Fee).ThenBy(z => z.Name).Select(z => new ShopZone(z.Name, z.Fee, z.Eta)).ToListAsync(ct);
                list.Add(new ShopStore(key, info.DisplayName, p?.LegalName ?? info.LegalName, whatsapp, p?.Phone, p?.Email, p?.Address, info.Website,
                    p?.OpeningHours, p?.ReplyPromise, delivery, info.Socials, p?.DefaultVatRate ?? 0, PayOnline(info)));
            }
            return list;
        }))!;
    }

    /// <summary>Whether the store can take a payment on the website (an online processor configured and allowed for it), so the button only shows where it works.</summary>
    private bool PayOnline(Inventory.Infrastructure.Services.CompanyInfo info)
    {
        var configured = HttpContext.RequestServices.GetRequiredService<Inventory.Application.Payments.PaymentGateways>().Configured;
        return configured.Any(p => info.PaymentProviders is not { } allowed || allowed.Contains(p, StringComparer.OrdinalIgnoreCase));
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
    /// <summary>One product's page: description, who it is for, analysis, feeding guide and today's price. Inactive products are not found.</summary>
    [HttpGet("products/{id:int}"), AllowAnonymous]
    public async Task<ActionResult<ShopProduct>> Product(int id, [FromQuery] string? store, CancellationToken ct)
    {
        if (Store(store) is not { } key) return NotFound();
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        var sp = HttpContext.RequestServices;
        var db = sp.GetRequiredService<IBusinessDbContext>();
        var p = await db.Products.AsNoTracking().Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        if (p is null) return NotFound();
        var item = (await sp.GetRequiredService<StockAvailabilityService>().CatalogueAsync(null, Inventory.Domain.PriceTiers.Retailer, ct)).FirstOrDefault(x => x.Id == id);
        var hasImage = await db.ProductImages.AsNoTracking().AnyAsync(i => i.ProductId == id, ct);
        Response.Headers.CacheControl = "public, max-age=60";
        return new ShopProduct(p.Id, p.Name, p.Unit, item?.Price ?? p.PriceRetail, (item?.Available ?? 0) > 0, item is { Available: > 0 and <= 5 }, hasImage, p.Category?.Name,
            p.Species, p.LifeStage, p.ProteinPct, p.FatPct, p.FiberPct, p.MoisturePct, p.NutritionSummary, p.Description, p.FeedingGuide);
    }

    /// <summary>Pay on the website: the basket becomes an order at today's prices and the visitor goes to the secure AlatPay page for that total.</summary>
    [HttpPost("checkout"), AllowAnonymous, RequestSizeLimit(16384), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<ActionResult<Inventory.Application.Website.CheckoutResult>> Checkout([FromQuery] string? store, Inventory.Application.Website.CheckoutRequest r, CancellationToken ct)
    {
        if (Store(store) is not { } key) return NotFound();
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        var site = config["Site:PublicUrl"] is { Length: > 0 } u ? u.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        return await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Website.WebCheckoutService>().CheckoutAsync(r, site + "/?paid=1#shop", ct);
    }

    /// <summary>Counts a visit, a basket started, a basket sent to WhatsApp or a checkout opened. Nothing about the visitor is stored.</summary>
    [HttpPost("track"), AllowAnonymous, RequestSizeLimit(1024), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<IActionResult> Track(TrackRequest r, CancellationToken ct)
    {
        if (Store(r.Store) is not { } key) return NoContent();
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Website.WebsiteService>().TrackAsync(r.Event ?? "", r.Amount, ct);
        return NoContent();
    }

    /// <summary>Approved reviews (first names only, published with the customer's consent).</summary>
    [HttpGet("reviews"), AllowAnonymous]
    public async Task<IReadOnlyList<Inventory.Application.Website.PublicReview>> Reviews([FromQuery] string? store, CancellationToken ct)
    {
        HttpContext.Items[RequestCompany.OverrideItem] = Store(store) ?? CompanyKey;
        Response.Headers.CacheControl = "public, max-age=300";
        return await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Website.WebsiteService>().PublicReviewsAsync(12, ct);
    }

    /// <summary>A visitor's review. Kept back until staff approve it.</summary>
    [HttpPost("reviews"), AllowAnonymous, RequestSizeLimit(8192), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<IActionResult> Review([FromQuery] string? store, Inventory.Application.Website.ReviewInput r, CancellationToken ct)
    {
        HttpContext.Items[RequestCompany.OverrideItem] = Store(store) ?? CompanyKey;
        await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Website.WebsiteService>().SubmitReviewAsync(r, ct);
        return NoContent();
    }

    /// <summary>Partners who gave permission for their logo to be shown.</summary>
    [HttpGet("partners"), AllowAnonymous]
    public async Task<IReadOnlyList<Inventory.Application.Website.PublicPartner>> Partners([FromQuery] string? store, CancellationToken ct)
    {
        HttpContext.Items[RequestCompany.OverrideItem] = Store(store) ?? CompanyKey;
        Response.Headers.CacheControl = "public, max-age=300";
        return await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Website.WebsiteService>().PublicPartnersAsync(ct);
    }

    [HttpGet("partners/{id:int}/logo"), AllowAnonymous]
    public async Task<IActionResult> PartnerLogo(int id, [FromQuery] string? store, CancellationToken ct)
    {
        HttpContext.Items[RequestCompany.OverrideItem] = Store(store) ?? CompanyKey;
        if (await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Website.WebsiteService>().PartnerLogoAsync(id, true, ct) is not { } logo) return NotFound();
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(logo.Data, logo.ContentType);
    }

    /// <summary>The latest published articles (feeding guides, pet care) from the Blog app, linked to their pages.</summary>
    [HttpGet("articles"), AllowAnonymous]
    public async Task<object> Articles([FromQuery] string? store, CancellationToken ct)
    {
        var key = Store(store) ?? CompanyKey;
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        Response.Headers.CacheControl = "public, max-age=300";
        var db = HttpContext.RequestServices.GetRequiredService<IBusinessDbContext>();
        var posts = await db.AppRecords.AsNoTracking().Where(r => r.App == "blog" && r.Status == "Published" && r.Code != null).OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(6)
            .Select(r => new { r.Title, r.Code, r.Date, r.Data }).ToListAsync(ct);
        return posts.Select(p =>
        {
            var d = System.Text.Json.Nodes.JsonNode.Parse(p.Data) as System.Text.Json.Nodes.JsonObject;
            var body = d?["body"]?.ToString() ?? "";
            var plain = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>|[#*_>`]", " ");
            plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
            return new { p.Title, Url = $"/api/site/{key}/blog/{p.Code}", p.Date, Excerpt = plain.Length > 180 ? plain[..180].TrimEnd() + "…" : plain };
        }).ToList();
    }
    [HttpPost("message"), AllowAnonymous, RequestSizeLimit(65536), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<WebChatResponse> Message(WebChatRequest r, CancellationToken ct)
    {
        var session = ValidSession(r.SessionId);
        var store = SalesAssistantEngine.UsesFlow(config)
            ? await HttpContext.RequestServices.GetRequiredService<ChatStoreRouter>().RouteAsync(CompanyKey, ChatChannels.WebChat, session, r.Message ?? "", ct)
            : CompanyKey;
        HttpContext.Items[RequestCompany.OverrideItem] = store;
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
        var now = DateTime.UtcNow;
        var sinceUtc = since.Kind == DateTimeKind.Local ? since.ToUniversalTime() : DateTime.SpecifyKind(since, DateTimeKind.Utc);
        // The visitor may be buying from any store the chat sells for, so look in each (a confirmation arrives in the store that sold).
        var messages = new List<(DateTime At, string Text)>();
        var awaiting = false;
        foreach (var key in HttpContext.RequestServices.GetRequiredService<ChatStoreRouter>().StoresFor(CompanyKey))
        {
            await using var db = registry.CreateFor(registry.Find(key)!);
            messages.AddRange((await (from m in db.ChatMessages.AsNoTracking()
                                      join c in db.ChatConversations.AsNoTracking() on m.ConversationId equals c.Id
                                      where c.Channel == ChatChannels.WebChat && c.ExternalId == session && m.Role == ChatRoles.Assistant && m.CreatedAt > sinceUtc
                                      orderby m.Id select new { m.CreatedAt, m.Text }).Take(20).ToListAsync(ct)).Select(m => (m.CreatedAt, m.Text)));
            awaiting |= await db.ChatConversations.AsNoTracking().AnyAsync(c => c.Channel == ChatChannels.WebChat && c.ExternalId == session && c.State == ChatStates.AwaitingPayment, ct);
        }
        return new WebChatUpdates(messages.OrderBy(m => m.At).Take(20).Select(m => m.Text).ToList(), awaiting, now);
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
