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
public sealed record ShopItem(int Id, string Name, string Unit, decimal Price, bool Few, bool HasImage);

/// <summary>
/// The chat on the landing page. Which engine answers is a setting (SalesAssistant:Engine): "ai" (the default, the AI sales assistant the site has used so far)
/// or "flow" (the word-based conversation also used on the Twilio WhatsApp number: AI only as a last resort, AlatPay bank account inside the chat, quotations,
/// delivery questions, Yoruba/Igbo/Hausa/Pidgin). There is no phone number on the web, so identity is a browser-generated session id. The company is fixed
/// to the landing page's own brand (WebChat:CompanyKey, default "chewypets").
/// </summary>
[ApiController, Route("api/webchat")]
public class WebChatController(IConfiguration config, Microsoft.Extensions.Options.IOptions<Inventory.Infrastructure.WhatsApp.TwilioOptions> twilio) : ControllerBase
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

    /// <summary>The landing page's shop: in-stock products with today's retail price, read from the same stock the chat sells from. Cached for a minute.</summary>
    [HttpGet("products"), AllowAnonymous]
    public async Task<IReadOnlyList<ShopItem>> Products([FromServices] Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=60";
        HttpContext.Items[RequestCompany.OverrideItem] = CompanyKey;
        return (await cache.GetOrCreateAsync("shop:" + CompanyKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var sp = HttpContext.RequestServices;
            var cat = await sp.GetRequiredService<StockAvailabilityService>().CatalogueAsync(null, Inventory.Domain.PriceTiers.Retailer, ct);
            var withImage = (await sp.GetRequiredService<IBusinessDbContext>().ProductImages.AsNoTracking().Select(i => i.ProductId).ToListAsync(ct)).ToHashSet();
            return cat.Where(p => p.Available > 0 && p.Price > 0).OrderBy(p => p.Name)
                .Select(p => new ShopItem(p.Id, p.Name, p.Unit, p.Price, p.Available <= 5, withImage.Contains(p.Id))).ToList();
        }))!;
    }

    /// <summary>A product's photo for the shop (only for active products). Cached by browsers for a day.</summary>
    [HttpGet("products/{id:int}/image"), AllowAnonymous]
    public async Task<IActionResult> ProductImage(int id, CancellationToken ct)
    {
        HttpContext.Items[RequestCompany.OverrideItem] = CompanyKey;
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
