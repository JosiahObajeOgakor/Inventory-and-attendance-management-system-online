using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Campaigns;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Controllers;

/// <summary>Switch campaigns on or off, set when they run, look at a sample of what they would say today, run one now, and see exactly what was sent. Administrators only.</summary>
[ApiController, Route("api/campaigns"), Authorize(Policy = Policies.Admin), RequiresNewFeatures]
public class CampaignsController(ICurrentUser cu, CampaignService svc, Inventory.Application.Queries.ChatFunnelQueries funnel) : AppController(cu)
{
    /// <summary>Who chats, from where, in what language, when, how far they get, and who can be followed up — for targeting the campaigns below.</summary>
    [HttpGet("insights")] public Task<Inventory.Application.Queries.ChatFunnel> Insights([FromQuery] int days, CancellationToken ct) => funnel.GetAsync(days == 0 ? 30 : days, ct);
    [HttpGet] public Task<List<CampaignDto>> List(CancellationToken ct) => svc.ListAsync(ct);
    [HttpPut("{id:int}")] public Task<CampaignDto> Update(int id, CampaignUpdate u, CancellationToken ct) => svc.UpdateAsync(id, u, Me.FullName, ct);
    [HttpPost("{id:int}/preview")] public Task<RunResult> Preview(int id, CancellationToken ct) => svc.PreviewAsync(id, ct);
    [HttpPost("{id:int}/run")] public Task<RunResult> Run(int id, CancellationToken ct) => svc.RunNowAsync(id, Me.FullName, ct);
    [HttpGet("log")] public Task<List<CampaignMessageDto>> Log([FromQuery] int? campaignId, [FromQuery] int take, CancellationToken ct) => svc.LogAsync(campaignId, take == 0 ? 100 : take, ct);
    [HttpGet("consent")] public Task<ConsentStats> Consent(CancellationToken ct) => svc.StatsAsync(ct);
}

/// <summary>The link at the bottom of every campaign email. Signed, so only the real recipient's link works; works with one click and without logging in.</summary>
[ApiController, Route("api/marketing")]
public class MarketingController(CompanyRegistry registry, IOptions<CampaignOptions> options) : ControllerBase
{
    [HttpGet("unsubscribe"), HttpPost("unsubscribe"), AllowAnonymous, EnableRateLimiting(Policies.WebChatRateLimit)]
    public async Task<IActionResult> Unsubscribe([FromQuery] string? k, [FromQuery] int c, [FromQuery] string? t, CancellationToken ct)
    {
        if (registry.Find(k) is null || !UnsubscribeLinks.Valid(options.Value.SecretKey, k!, c, t))
            return Page(400, "This unsubscribe link isn't valid.", "Please reply STOP in WhatsApp, or contact the shop.");
        HttpContext.Items[RequestCompany.OverrideItem] = k;
        var db = HttpContext.RequestServices.GetRequiredService<IBusinessDbContext>();
        var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == c, ct);
        if (customer is not null && customer.MarketingStatus != MarketingStatuses.OptedOut)
            await HttpContext.RequestServices.GetRequiredService<ConsentService>().SetAsync(customer, MarketingStatuses.OptedOut, "email-link", null, ct);
        return Page(200, "You're unsubscribed.", "You won't receive reminders or offers from us any more. If this was a mistake, send SUBSCRIBE to us on WhatsApp.");
    }

    private ContentResult Page(int status, string title, string body) => new()
    {
        StatusCode = status, ContentType = "text/html; charset=utf-8",
        Content = $"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>{System.Net.WebUtility.HtmlEncode(title)}</title>"
                + $"<body style=\"font:16px system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1rem\"><h1>{System.Net.WebUtility.HtmlEncode(title)}</h1><p>{System.Net.WebUtility.HtmlEncode(body)}</p></body>",
    };
}
