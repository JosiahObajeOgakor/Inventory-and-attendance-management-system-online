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

        var msg = value?["messages"]?[0];
        if (msg is null) return Ok();   // delivery/read receipts etc. — ack and ignore
        var wamid = msg["id"]?.GetValue<string>() ?? "";
        var from = msg["from"]?.GetValue<string>() ?? "";
        var text = msg["text"]?["body"]?.GetValue<string>();
        if (string.IsNullOrEmpty(text) || from.Length == 0) return Ok();   // non-text message (image/audio/etc.) — ack, don't crash

        var dedup = HttpContext.RequestServices.GetRequiredService<WebhookDedupService>();
        if (await dedup.SeenAsync("whatsapp-in", wamid, ct)) return Ok();   // Meta redelivered this; already handled

        var assistant = HttpContext.RequestServices.GetRequiredService<SalesAssistantService>();
        var reply = await assistant.AskAsync(Inventory.Domain.Entities.ChatChannels.WhatsApp, from, text, ct);
        await wa.SendTextAsync(cfg.CompanyKey, from, reply.Text, ct);
        return Ok();
    }
}
