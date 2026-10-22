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

        var from = TwilioWhatsAppClient.Digits(form["From"]);
        var body = form["Body"].ToString();
        if (from.Length == 0) return Twiml([]);
        // One line sells for every store: the message goes to the store whose stock it is about (see ChatStoreRouter).
        var store = await HttpContext.RequestServices.GetRequiredService<ChatStoreRouter>().RouteAsync(cfg.CompanyKey, ChatChannels.WhatsApp, from, body, ct);
        HttpContext.Items[RequestCompany.OverrideItem] = store;

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


