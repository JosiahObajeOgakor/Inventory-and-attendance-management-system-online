using System.Text.Json.Nodes;
using Inventory.Api.Infrastructure;
using Inventory.Application.SalesAssistant;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Inventory.Api.Controllers;

/// <summary>
/// Meta calls this for the WhatsApp Business number(s) configured in WhatsApp__Numbers. Modeled directly on
/// PaystackWebhookController: verify first, resolve which company this number belongs to, set the company
/// override the same way the Paystack webhook does, ack-and-ignore anything not understood.
/// </summary>
[ApiController, Route("api/whatsapp")]
public class WhatsAppController(MetaWhatsAppClient wa, CompanyRegistry registry, ILogger<WhatsAppController> log) : ControllerBase
{
    [HttpGet("webhook"), AllowAnonymous]
    public IActionResult Verify([FromQuery(Name = "hub.mode")] string? mode, [FromQuery(Name = "hub.verify_token")] string? token, [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && wa.VerifyToken(token) && !string.IsNullOrEmpty(challenge)) return Content(challenge);
        return Forbid();
    }

    [HttpPost("webhook"), AllowAnonymous, RequestSizeLimit(1_000_000), EnableRateLimiting(Policies.WhatsAppRateLimit)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8)) raw = await reader.ReadToEndAsync(ct);

        JsonNode? root;
        try { root = JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { return Ok(); }
        var value = root?["entry"]?[0]?["changes"]?[0]?["value"];
        var phoneNumberId = value?["metadata"]?["phone_number_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(phoneNumberId)) return Ok();   // not a shape we recognise (e.g. a status callback) — ack and ignore

        if (!wa.IsValidSignature(phoneNumberId, raw, Request.Headers["X-Hub-Signature-256"].FirstOrDefault())) return Unauthorized();

        var cfg = wa.ForPhoneNumberId(phoneNumberId);
        if (cfg is null || registry.Find(cfg.CompanyKey) is null) { log.LogWarning("WhatsApp webhook for an unmapped phone number id"); return Ok(); }
        HttpContext.Items[RequestCompany.OverrideItem] = cfg.CompanyKey;

        // Delivery reports. A message Meta accepted can still fail (most often: the person hasn't written to us in 24 hours, or the app
        // isn't published yet), and the only place that is ever said is here — so a failure is logged with Meta's own reason.
        if (value?["statuses"] is JsonArray statuses)
        {
            foreach (var s in statuses)
            {
                var state = s?["status"]?.GetValue<string>();
                if (state is not "failed") continue;
                var err = s?["errors"]?[0];
                log.LogWarning("WhatsApp did NOT deliver message {MessageId} to {To}: {Code} {Title}. {Detail}",
                    s?["id"]?.GetValue<string>() ?? "?", s?["recipient_id"]?.GetValue<string>() ?? "?",
                    err?["code"]?.ToJsonString() ?? "?", err?["title"]?.GetValue<string>() ?? "",
                    err?["error_data"]?["details"]?.GetValue<string>() ?? err?["message"]?.GetValue<string>() ?? "");
            }
        }

        var msg = value?["messages"]?[0];
        if (msg is null) return Ok();   // read receipts and the statuses handled above — ack and ignore
        var wamid = msg["id"]?.GetValue<string>() ?? "";
        var from = msg["from"]?.GetValue<string>() ?? "";
        var text = msg["text"]?["body"]?.GetValue<string>();
        var buttonId = msg["interactive"]?["button_reply"]?["id"]?.GetValue<string>();
        if ((string.IsNullOrEmpty(text) && string.IsNullOrEmpty(buttonId)) || from.Length == 0) return Ok();   // image/audio/etc. — ack, don't crash

        // Store it and acknowledge Meta straight away; the inbox worker answers it (with retries) in the background.
        // Meta redelivers anything that isn't acknowledged quickly, and the inbox ignores a message id it already has.
        var inbox = HttpContext.RequestServices.GetRequiredService<WhatsAppInbox>();
        if (await inbox.EnqueueAsync(Inventory.Domain.Entities.ChatChannels.WhatsApp, from, wamid.Length > 0 ? wamid : Guid.NewGuid().ToString("N"), text, buttonId, ct))
            InboxSignal.Wake();
        return Ok();
    }
}
