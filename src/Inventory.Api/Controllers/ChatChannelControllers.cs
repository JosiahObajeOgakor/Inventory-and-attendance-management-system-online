using System.Security;
using System.Text.Json.Nodes;
using Inventory.Api.Infrastructure;
using Inventory.Application.Payments;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Inventory.Api.Controllers;

/// <summary>
/// Twilio calls this for every WhatsApp message sent to our number. The reply goes back in the HTTP response as TwiML, so answering the customer
/// needs no second API call. Verified by Twilio's signature (HMAC-SHA1 of the exact public URL plus all form fields, keyed with the auth token) before anything else happens.
/// </summary>
[ApiController, Route("api/twilio")]
public class TwilioWhatsAppController(TwilioWhatsAppClient twilio, CompanyRegistry registry, ILogger<TwilioWhatsAppController> log) : ControllerBase
{
    [HttpPost("whatsapp"), AllowAnonymous, RequestSizeLimit(65536), EnableRateLimiting(Policies.WhatsAppRateLimit)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest();
        var form = await Request.ReadFormAsync(ct);
        var fields = form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())).ToList();
        var url = twilio.SignedUrl("/api/twilio/whatsapp", $"{Request.Scheme}://{Request.Host}{Request.Path}");
        if (!twilio.IsValidSignature(url, fields, Request.Headers["X-Twilio-Signature"].FirstOrDefault())) return Unauthorized();

        var cfg = twilio.ForToNumber(form["To"]);
        if (cfg is null || registry.Find(cfg.CompanyKey) is null) { log.LogWarning("Twilio message for an unmapped WhatsApp number"); return Twiml([]); }
        HttpContext.Items[RequestCompany.OverrideItem] = cfg.CompanyKey;

        var from = TwilioWhatsAppClient.Digits(form["From"]);
        var body = form["Body"].ToString();
        if (from.Length == 0) return Twiml([]);

        var dedup = HttpContext.RequestServices.GetRequiredService<WebhookDedupService>();
        if (await dedup.SeenAsync("twilio-in", form["MessageSid"].ToString(), ct)) return Twiml([]);   // Twilio redelivered; already answered

        if (string.IsNullOrWhiteSpace(body)) return Twiml([ChatText.Get(ChatLanguages.English, "text_only")]);

        var flow = HttpContext.RequestServices.GetRequiredService<ChatFlowService>();
        var reply = await flow.HandleAsync(ChatChannels.WhatsApp, from, form["ProfileName"].ToString(), body, ct);
        return Twiml(reply.Messages);
    }

    /// <summary>One TwiML &lt;Message&gt; per reply bubble.</summary>
    private ContentResult Twiml(IEnumerable<string> messages) => Content(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response>" + string.Concat(messages.Select(m => $"<Message>{SecurityElement.Escape(m)}</Message>")) + "</Response>",
        "text/xml");
}

/// <summary>
/// AlatPay calls this when a transfer into one of our virtual accounts completes. The body is signed (HMAC-SHA256, Base64, x-signature header). A webhook is
/// only a prompt to look: <see cref="PaymentLinkService.SettleAsync"/> asks AlatPay itself whether the money arrived and for how much before recording anything,
/// and is safe to run any number of times (AlatPay retries deliveries).
/// </summary>
[ApiController, Route("api/alatpay")]
public class AlatPayWebhookController(IVirtualAccountGateway alat, CompanyRegistry registry, ILogger<AlatPayWebhookController> log) : ControllerBase
{
    [HttpPost("webhook"), AllowAnonymous, RequestSizeLimit(65536)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8)) raw = await reader.ReadToEndAsync(ct);
        if (!alat.IsValidSignature(raw, Request.Headers["x-signature"].FirstOrDefault())) return Unauthorized();

        string? orderId, status;
        try
        {
            var root = JsonNode.Parse(raw);
            // The payload is PascalCase and nested (Value.Data.*) per AlatPay's docs; accept camelCase too.
            var data = root?["Value"]?["Data"] ?? root?["value"]?["data"] ?? root?["Data"] ?? root?["data"];
            orderId = (data?["OrderId"] ?? data?["orderId"])?.ToString();
            status = (data?["Status"] ?? data?["status"])?.ToString();
        }
        catch (System.Text.Json.JsonException) { return BadRequest(); }
        if (string.IsNullOrEmpty(orderId)) return Ok();
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) && !string.Equals(status, "successful", StringComparison.OrdinalIgnoreCase)) return Ok();

        var key = orderId.Split('-')[0];
        if (registry.Find(key) is null) { log.LogWarning("AlatPay webhook for an unknown company prefix"); return Ok(); }
        HttpContext.Items[RequestCompany.OverrideItem] = key;

        var result = await HttpContext.RequestServices.GetRequiredService<PaymentLinkService>().SettleAsync(orderId, ct);
        log.LogInformation("AlatPay webhook: applied={Applied} already={Already} unfulfilled={Unfulfilled}", result.Applied, result.AlreadySettled, result.Unfulfilled);
        await HttpContext.RequestServices.GetRequiredService<ChatPaymentNotifier>().NotifyAsync(result, ct);
        return Ok();
    }
}
