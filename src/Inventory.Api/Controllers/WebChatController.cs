using Inventory.Api.Infrastructure;
using Inventory.Application.Common;
using Inventory.Application.SalesAssistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Inventory.Api.Controllers;

public sealed record WebChatRequest(string SessionId, string Message);
public sealed record WebChatResponse(string Reply, string? CheckoutUrl, string? QuotationNumber);

/// <summary>
/// The chat widget on the landing page (chewypetfeeds.com) — the same sales assistant as WhatsApp, over a
/// stateless JSON endpoint instead. There is no signed-in user and no phone number, so identity is a
/// client-generated session id, and the company is fixed to "chewypets" (the landing page's own brand) via
/// the same HttpContext.Items override the webhooks use — there is no JWT to resolve it from on an anonymous request.
/// </summary>
[ApiController, Route("api/webchat")]
public class WebChatController : ControllerBase
{
    private const string Company = "chewypets";

    [HttpGet("status"), AllowAnonymous]
    public object Status() => new { configured = true };

    [HttpPost("message"), AllowAnonymous, RequestSizeLimit(65536), EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<WebChatResponse> Message(WebChatRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.SessionId) || r.SessionId.Length > 100) throw new BusinessRuleException("Missing or invalid session id.");
        HttpContext.Items[Inventory.Api.Infrastructure.RequestCompany.OverrideItem] = Company;
        var assistant = HttpContext.RequestServices.GetRequiredService<SalesAssistantService>();
        var reply = await assistant.AskAsync(Inventory.Domain.Entities.ChatChannels.WebChat, r.SessionId.Trim(), r.Message, ct);
        return new WebChatResponse(reply.Text, reply.CheckoutUrl, reply.QuotationNumber);
    }
}
