using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Common;
using Inventory.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.WhatsApp;

/// <summary>One WhatsApp Business phone number: which company it belongs to, and the Meta credentials for it.
/// A second entry (a different number/App) is how Candid Purrfect gets its own WhatsApp line later, with no code change.</summary>
public sealed class WhatsAppNumberConfig
{
    public string PhoneNumberId { get; set; } = "";
    public string CompanyKey { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string BusinessAccountId { get; set; } = "";
}

public sealed class WhatsAppOptions
{
    public const string Section = "WhatsApp";
    /// <summary>Chosen once by whoever sets this up; entered again in Meta's webhook configuration screen.</summary>
    public string VerifyToken { get; set; } = "";
    public List<WhatsAppNumberConfig> Numbers { get; set; } = [];
}

/// <summary>WhatsApp Business Cloud API over HTTPS. Styled on PaystackGateway: raw HttpClient, no SDK.</summary>
public sealed class MetaWhatsAppClient(HttpClient http, IOptions<WhatsAppOptions> options, ILogger<MetaWhatsAppClient> log) : IWhatsAppSender
{
    private const string GraphBase = "https://graph.facebook.com/v21.0/";
    private WhatsAppOptions Opt => options.Value;

    public WhatsAppNumberConfig? ForCompany(string companyKey) =>
        Opt.Numbers.FirstOrDefault(n => n.CompanyKey.Equals(companyKey, StringComparison.OrdinalIgnoreCase));

    public WhatsAppNumberConfig? ForPhoneNumberId(string phoneNumberId) =>
        Opt.Numbers.FirstOrDefault(n => n.PhoneNumberId == phoneNumberId);

    public bool IsConfigured(string companyKey) => ForCompany(companyKey) is { AccessToken.Length: > 0 };

    public bool VerifyToken(string? token) => Opt.VerifyToken.Length > 0 && token == Opt.VerifyToken;

    /// <summary>Meta signs the raw body with HMAC-SHA256 using the App Secret, hex-encoded, header X-Hub-Signature-256: sha256=&lt;hex&gt;.</summary>
    public bool IsValidSignature(string phoneNumberId, string rawBody, string? header)
    {
        var cfg = ForPhoneNumberId(phoneNumberId);
        if (cfg is null || string.IsNullOrEmpty(cfg.AppSecret) || string.IsNullOrEmpty(header)) return false;
        var mac = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.AppSecret), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(header.Trim().ToLowerInvariant()));
    }

    public async Task SendTextAsync(string companyKey, string toE164, string text, CancellationToken ct)
    {
        var cfg = ForCompany(companyKey) ?? throw new BusinessRuleException("WhatsApp isn't set up for this business.");
        var body = new JsonObject { ["messaging_product"] = "whatsapp", ["to"] = toE164, ["type"] = "text", ["text"] = new JsonObject { ["body"] = text } };
        await SendAsync(cfg, body, ct);
    }

    public async Task SendDocumentAsync(string companyKey, string toE164, byte[] pdf, string filename, string caption, CancellationToken ct)
    {
        var cfg = ForCompany(companyKey) ?? throw new BusinessRuleException("WhatsApp isn't set up for this business.");
        var mediaId = await UploadMediaAsync(cfg, pdf, filename, "application/pdf", ct);
        var body = new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["to"] = toE164, ["type"] = "document",
            ["document"] = new JsonObject { ["id"] = mediaId, ["filename"] = filename, ["caption"] = caption },
        };
        await SendAsync(cfg, body, ct);
    }

    public async Task SendButtonsAsync(string companyKey, string toE164, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct)
    {
        var cfg = ForCompany(companyKey) ?? throw new BusinessRuleException("WhatsApp isn't set up for this business.");
        var replies = new JsonArray();
        foreach (var b in buttons.Take(3))
            replies.Add(new JsonObject { ["type"] = "reply", ["reply"] = new JsonObject { ["id"] = b.Id, ["title"] = b.Title.Length > 20 ? b.Title[..20] : b.Title } });
        var msg = new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["to"] = toE164, ["type"] = "interactive",
            ["interactive"] = new JsonObject
            {
                ["type"] = "button",
                ["body"] = new JsonObject { ["text"] = body.Length > 1024 ? body[..1024] : body },
                ["action"] = new JsonObject { ["buttons"] = replies },
            },
        };
        await SendAsync(cfg, msg, ct);
    }

    public async Task<bool> SendTemplateAsync(string companyKey, string toE164, string templateName, string languageCode, IReadOnlyList<string> bodyParameters, CancellationToken ct)
    {
        var cfg = ForCompany(companyKey) ?? throw new BusinessRuleException("WhatsApp isn't set up for this business.");
        var parameters = new JsonArray();
        // Template parameters can't contain newlines, tabs or 4+ spaces in a row (Meta rejects the send).
        foreach (var p in bodyParameters)
            parameters.Add(new JsonObject { ["type"] = "text", ["text"] = System.Text.RegularExpressions.Regex.Replace(string.IsNullOrWhiteSpace(p) ? "-" : p, @"\s+", " ").Trim() });
        var msg = new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["to"] = toE164, ["type"] = "template",
            ["template"] = new JsonObject
            {
                ["name"] = templateName,
                ["language"] = new JsonObject { ["code"] = languageCode },
                ["components"] = new JsonArray { new JsonObject { ["type"] = "body", ["parameters"] = parameters } },
            },
        };
        return await SendAsync(cfg, msg, ct);
    }

    private async Task<bool> SendAsync(WhatsAppNumberConfig cfg, JsonObject body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, GraphBase + cfg.PhoneNumberId + "/messages")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return true;
            log.LogWarning("WhatsApp send failed: {Status} {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { log.LogWarning(ex, "WhatsApp send failed"); return false; }
    }

    private async Task<string> UploadMediaAsync(WhatsAppNumberConfig cfg, byte[] bytes, string filename, string mime, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("whatsapp"), "messaging_product" },
            { new StringContent(mime), "type" },
        };
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(fileContent, "file", filename);

        using var req = new HttpRequestMessage(HttpMethod.Post, GraphBase + cfg.PhoneNumberId + "/media") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) { log.LogWarning("WhatsApp media upload failed: {Status}", (int)res.StatusCode); throw new BusinessRuleException("Could not send the document over WhatsApp right now."); }
        var id = JsonNode.Parse(text)?["id"]?.GetValue<string>();
        return string.IsNullOrEmpty(id) ? throw new BusinessRuleException("Could not send the document over WhatsApp right now.") : id;
    }
}
