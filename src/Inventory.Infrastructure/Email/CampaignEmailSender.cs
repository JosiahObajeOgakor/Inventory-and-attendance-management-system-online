using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Email;
using Inventory.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Email;

public sealed class SendGridOptions
{
    public const string Section = "SendGrid";
    /// <summary>SG.… key with "Mail Send" permission. Environment only (SendGrid__ApiKey).</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>A verified sender or an address on an authenticated domain, or SendGrid will refuse the message.</summary>
    public string FromEmail { get; set; } = "";
}

/// <summary>Campaign email through SendGrid (Twilio's email service) when a key is set, otherwise through the existing SMTP mailbox. Every message carries List-Unsubscribe.</summary>
public sealed class CampaignEmailSender(HttpClient http, IOptions<SendGridOptions> options, IEmailSender smtp, ILogger<CampaignEmailSender> log) : ICampaignEmailSender
{
    private SendGridOptions Opt => options.Value;
    private bool UseSendGrid => Opt.ApiKey.Length > 0 && Opt.FromEmail.Contains('@');
    public bool IsConfigured => UseSendGrid || smtp.IsConfigured;

    public async Task<bool> TrySendAsync(string toEmail, string? toName, string fromName, string subject, string html, string text, string unsubscribeUrl, CancellationToken ct)
    {
        try
        {
            if (!UseSendGrid)
            {
                await smtp.SendAsync(new EmailMessage(toEmail, toName, subject, html, text, [], fromName, null), ct);
                return true;
            }
            var body = new JsonObject
            {
                ["personalizations"] = new JsonArray(new JsonObject { ["to"] = new JsonArray(new JsonObject { ["email"] = toEmail, ["name"] = toName ?? "" }) }),
                ["from"] = new JsonObject { ["email"] = Opt.FromEmail, ["name"] = fromName },
                ["subject"] = subject,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text/plain", ["value"] = text }, new JsonObject { ["type"] = "text/html", ["value"] = html }),
            };
            if (unsubscribeUrl.Length > 0) body["headers"] = new JsonObject { ["List-Unsubscribe"] = $"<{unsubscribeUrl}>", ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click" };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Opt.ApiKey);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) log.LogWarning("SendGrid refused a message: {Status} {Body}", (int)res.StatusCode, (await res.Content.ReadAsStringAsync(ct)) is { Length: > 300 } b ? b[..300] : "");
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { log.LogWarning(ex, "Campaign email failed"); return false; }
    }
}
