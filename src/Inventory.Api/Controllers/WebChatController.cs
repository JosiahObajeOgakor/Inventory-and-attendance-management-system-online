using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

public sealed record WebChatRequest(string SessionId, string Message);
/// <param name="Reply">All of the bot's messages for this turn joined by blank lines (older widgets read only this).</param>
/// <param name="Replies">The same messages one by one, so the page can show them as separate bubbles.</param>
/// <param name="AwaitingPayment">True while a payment account has been given and not yet paid: the page then checks <c>updates</c> for the confirmation.</param>
/// <param name="At">Server time of this reply; pass it back to <c>updates</c> as <c>since</c>.</param>
public sealed record WebChatResponse(string Reply, string? CheckoutUrl, string? QuotationNumber, IReadOnlyList<string> Replies, bool AwaitingPayment, DateTime At);
public sealed record WebChatUpdates(IReadOnlyList<string> Messages, bool AwaitingPayment, DateTime At);

/// <summary>
/// The chat on the landing page. It runs the SAME conversation engine as WhatsApp (word-based, the AI only as a last resort, AlatPay bank account inside the chat),
/// so a sale behaves identically on both. There is no phone number on the web, so the customer is asked for their WhatsApp number once; identity is otherwise a
/// browser-generated session id. The company is fixed to the landing page's own brand (WebChat:CompanyKey, default "chewypets").
/// </summary>
[ApiController, Route("api/webchat")]
public class WebChatController(IConfiguration config) : ControllerBase
{
    private string CompanyKey => config["WebChat:CompanyKey"] is { Length: > 0 } k ? k : "chewypets";

    [HttpGet("status"), AllowAnonymous]
    public object Status() => new { configured = true };

    [HttpPost("message"), AllowAnonymous, RequestSizeLimit(65536), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<WebChatResponse> Message(WebChatRequest r, CancellationToken ct)
    {
        var session = ValidSession(r.SessionId);
        HttpContext.Items[RequestCompany.OverrideItem] = CompanyKey;
        var flow = HttpContext.RequestServices.GetRequiredService<ChatFlowService>();
        var reply = await flow.HandleAsync(ChatChannels.WebChat, session, "", r.Message ?? "", ct);
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
