using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Application.Analytics;
using Inventory.Application.Dashboard;
using Inventory.Application.Email;
using Inventory.Infrastructure.Localization;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public sealed record AskRequest(string Question, List<ChatTurn>? History);
public sealed record SendQuotationRequest(string? To, string? Note);
public sealed record SendPriceListRequest(int? CustomerId, string? Tier, string? To, string? Note);

/// <summary>The assistant reads cost, profit and debt, so like the Finance screen it is for administrators only.</summary>
[ApiController, Route("api/ai"), Authorize(Policy = Policies.Admin)]
public class AiController(ICurrentUser cu, AssistantService assistant, InsightService insights) : AppController(cu)
{
    [HttpGet("status")]
    public Task<AiStatus> Status(CancellationToken ct) => assistant.StatusAsync(Me.Id, ct);

    [HttpPost("ask")]
    public async Task<AssistantAnswer> Ask(AskRequest r, CancellationToken ct) => await assistant.AskAsync(Me, r.Question, r.History, ct);

    [HttpGet("insights")]
    public Task<InsightSet> Insights([FromQuery] bool refresh, CancellationToken ct) => insights.GetAsync(refresh, ct);
}

[ApiController, Route("api/dashboard"), Authorize(Policy = Policies.Admin)]
public class DashboardOverviewController(ICurrentUser cu, OverviewQueries q) : AppController(cu)
{
    [HttpGet("overview")]
    public Task<Overview> Overview([FromQuery] int days = 30, CancellationToken ct = default) => q.GetAsync(days, ct);

    [HttpGet("calendar")]
    public Task<Calendar> Calendar([FromQuery] int year, [FromQuery] int month, CancellationToken ct) => q.CalendarAsync(year, month, ct);
}

/// <summary>Email a quotation or the price list with the PDF attached. Staff can send; the template preview shows exactly what the customer will get.</summary>
[ApiController, Route("api/email")]
public class EmailController(ICurrentUser cu, DocumentEmailService svc, IEmailSender sender) : AppController(cu)
{
    [HttpGet("status"), Authorize(Policy = Policies.Staff)]
    public object Status() => new { configured = sender.IsConfigured };

    [HttpPost("quotations/{id:int}/preview"), Authorize(Policy = Policies.Staff)]
    public Task<EmailPreview> PreviewQuotation(int id, SendQuotationRequest r, CancellationToken ct) => svc.PreviewQuotationAsync(id, r.To, r.Note, Me, ct);

    [HttpPost("quotations/{id:int}/send"), Authorize(Policy = Policies.Staff)]
    public async Task<EmailSent> SendQuotation(int id, SendQuotationRequest r, CancellationToken ct) => await svc.SendQuotationAsync(id, r.To, r.Note, Me, ct);

    [HttpPost("price-list/preview"), Authorize(Policy = Policies.Staff)]
    public Task<EmailPreview> PreviewPriceList(SendPriceListRequest r, CancellationToken ct) => svc.PreviewPriceListAsync(r.CustomerId, r.Tier, r.To, r.Note, Me, ct);

    [HttpPost("price-list/send"), Authorize(Policy = Policies.Staff)]
    public async Task<EmailSent> SendPriceList(SendPriceListRequest r, CancellationToken ct) => await svc.SendPriceListAsync(r.CustomerId, r.Tier, r.To, r.Note, Me, ct);
}

public sealed record SaleAdviceRequest(int? CustomerId, List<SaleLineIn> Lines);
public sealed record PurchaseAdviceRequest(int SupplierId, List<PurchaseLineIn> Lines);

/// <summary>Forecasts, products that sell together and unusual movements are for administrators (they show cost and demand); advice at the till is for anyone selling.</summary>
[ApiController, Route("api/analytics")]
public class AnalyticsController(ICurrentUser cu, AnalyticsService svc) : AppController(cu)
{
    [HttpGet("forecast"), Authorize(Policy = Policies.Admin)]
    public Task<List<ForecastRow>> Forecast(CancellationToken ct) => svc.ForecastAsync(ct);

    [HttpGet("sold-together"), Authorize(Policy = Policies.Admin)]
    public Task<List<BasketRuleRow>> SoldTogether(CancellationToken ct) => svc.SoldTogetherAsync(ct);

    [HttpGet("unusual"), Authorize(Policy = Policies.Admin)]
    public Task<List<UnusualRow>> Unusual(CancellationToken ct) => svc.UnusualMovementsAsync(ct);

    [HttpGet("suggested-order/{supplierId:int}"), Authorize(Policy = Policies.Admin)]
    public Task<List<SuggestedLine>> SuggestedOrder(int supplierId, CancellationToken ct) => svc.SuggestOrderAsync(supplierId, ct);

    [HttpPost("sale-advice"), Authorize(Policy = Policies.Staff)]
    public Task<List<AdviceLine>> SaleAdvice(SaleAdviceRequest r, CancellationToken ct) => svc.SaleAdviceAsync(r.CustomerId, r.Lines ?? [], ct);

    [HttpPost("purchase-advice"), Authorize(Policy = Policies.Admin)]
    public Task<List<AdviceLine>> PurchaseAdvice(PurchaseAdviceRequest r, CancellationToken ct) => svc.PurchaseAdviceAsync(r.SupplierId, r.Lines ?? [], ct);
}

public sealed record ArchiveRequest(DateOnly Cutoff, string Confirm);

/// <summary>Database size and archive. Administrators only; archiving is destructive, so it needs the word ARCHIVE typed and refuses recent dates.</summary>
[ApiController, Route("api/admin/archive"), Authorize(Policy = Policies.Admin)]
public class ArchiveController(ICurrentUser cu, ArchiveService svc) : AppController(cu)
{
    [HttpGet("usage")]
    public Task<DbUsage> Usage(CancellationToken ct) => svc.UsageAsync(ct);

    [HttpGet("preview")]
    public Task<List<ArchivePreviewRow>> Preview([FromQuery] DateOnly cutoff, CancellationToken ct) => svc.PreviewAsync(cutoff, ct);

    [HttpPost]
    public async Task<IActionResult> Run(ArchiveRequest r, CancellationToken ct)
    {
        if (!string.Equals(r.Confirm?.Trim(), "ARCHIVE", StringComparison.Ordinal))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Type ARCHIVE to confirm." });
        return Ok(await svc.ArchiveAsync(r.Cutoff, Me, ct));
    }

    [HttpGet("files")]
    public IReadOnlyList<ArchiveFile> Files() => svc.Files();

    [HttpGet("files/{name}")]
    public IActionResult Download(string name)
    {
        var path = svc.PathOf(name);
        if (path is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, "application/zip", name);
    }
}

/// <summary>The language catalogue. Public on purpose (the sign-in page is translated too); it holds interface wording only.</summary>
[ApiController, Route("api/i18n")]
public class LanguageController(LanguageCatalogue catalogue) : ControllerBase
{
    [HttpGet("languages"), AllowAnonymous]
    public IReadOnlyList<LanguageInfo> Languages() => LanguageCatalogue.Languages;

    [HttpGet("{code}"), AllowAnonymous]
    public IActionResult Catalogue(string code)
    {
        if (!LanguageCatalogue.Languages.Any(l => l.Code == code)) return NotFound();
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(catalogue.Get(code));
    }
}

/// <summary>
/// Paystack calls this when a customer pays. Three independent checks stand between it and the books: the HMAC signature on the body, the reference
/// (which must be one this server created), and a server-to-server confirmation from Paystack of the amount. Company routing comes from the reference prefix.
/// </summary>
[ApiController, Route("api/paystack")]
public class PaystackWebhookController(Inventory.Application.Payments.IPaymentGateway gateway, CompanyRegistry registry, ILogger<PaystackWebhookController> log) : ControllerBase
{
    [HttpPost("webhook"), AllowAnonymous, RequestSizeLimit(65536)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8)) raw = await reader.ReadToEndAsync(ct);
        if (!gateway.IsValidSignature(raw, Request.Headers["x-paystack-signature"].FirstOrDefault())) return Unauthorized();

        string? evt = null, reference = null;
        try { var root = System.Text.Json.Nodes.JsonNode.Parse(raw); evt = root?["event"]?.GetValue<string>(); reference = root?["data"]?["reference"]?.GetValue<string>(); }
        catch (System.Text.Json.JsonException) { return BadRequest(); }
        if (evt != "charge.success" || string.IsNullOrEmpty(reference)) return Ok();   // other events are acknowledged and ignored

        var key = reference.Split('-')[0];
        if (registry.Find(key) is null) { log.LogWarning("Payment webhook for an unknown company prefix"); return Ok(); }
        HttpContext.Items[RequestCompany.OverrideItem] = key;
        var result = await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Payments.PaymentLinkService>().SettleAsync(reference, ct);
        log.LogInformation("Paystack webhook: applied={Applied} already={Already}", result.Applied, result.AlreadySettled);
        if (result.Applied)
            await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Payments.PaymentFollowUpService>().SendReceiptIfPossibleAsync(result, ct);
        return Ok();
    }
}
