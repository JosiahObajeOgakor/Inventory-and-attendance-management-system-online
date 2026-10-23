using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Inventory.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.WhatsApp;

/// <summary>One WhatsApp sender on the Twilio account (the sandbox number while testing, an approved sender in production) and the company that owns it.</summary>
public sealed class TwilioNumberConfig
{
    /// <summary>E.164 with or without the "whatsapp:" prefix, e.g. +14155238886 (the Twilio sandbox number).</summary>
    public string From { get; set; } = "";
    public string CompanyKey { get; set; } = "";
}

public sealed class TwilioOptions
{
    public const string Section = "Twilio";
    public string AccountSid { get; set; } = "";
    /// <summary>Environment only (Twilio__AuthToken). Also the key Twilio signs webhooks with.</summary>
    public string AuthToken { get; set; } = "";
    /// <summary>The public https address Twilio calls, with no trailing slash, e.g. https://abc123.ngrok-free.app. Needed because the signature covers the exact URL and the API sits behind a proxy.</summary>
    public string PublicBaseUrl { get; set; } = "";
    public List<TwilioNumberConfig> Numbers { get; set; } = [];
    /// <summary>Approved WhatsApp templates (Twilio Content SIDs, HX…) by campaign type: Twilio__Templates__Reorder, __UnpaidOrder, __Promo, __AdminRestock. Needed to message anyone outside the 24-hour window.</summary>
    public Dictionary<string, string> Templates { get; set; } = [];
    /// <summary>Businesses without a number of their own that use the first number (one line sells for every business): comma-separated keys, or "*" (the default) for all.</summary>
    public string SharedWith { get; set; } = "*";
    /// <summary>SMS sender on the same account: an SMS-capable Twilio number (+1…) or an approved alphanumeric sender ID. Empty = no Twilio SMS.</summary>
    public string SmsFrom { get; set; } = "";
    /// <summary>Alternative to <see cref="SmsFrom"/>: a Twilio Messaging Service (MG…), which picks the sender per country.</summary>
    public string SmsMessagingServiceSid { get; set; } = "";
}

/// <summary>Twilio's WhatsApp channel over plain HTTPS (no SDK, same style as the other gateways).</summary>
public sealed class TwilioWhatsAppClient(HttpClient http, IOptions<TwilioOptions> options, ILogger<TwilioWhatsAppClient> log) : IChatSender
{
    private TwilioOptions Opt => options.Value;

    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>The business's own number; failing that, the shared line (see <see cref="TwilioOptions.SharedWith"/>).</summary>
    public TwilioNumberConfig? ForCompany(string companyKey) =>
        Opt.Numbers.FirstOrDefault(n => n.CompanyKey.Equals(companyKey, StringComparison.OrdinalIgnoreCase))
        ?? (Shares(companyKey) ? Opt.Numbers.FirstOrDefault(n => Digits(n.From).Length > 0) : null);

    private bool Shares(string companyKey) =>
        Opt.SharedWith.Trim() == "*" || Opt.SharedWith.Split([",", ";", " "], StringSplitOptions.RemoveEmptyEntries).Contains(companyKey, StringComparer.OrdinalIgnoreCase);
    /// <summary>The number the customer wrote TO ("whatsapp:+14155238886") decides which company the message is for.</summary>
    public TwilioNumberConfig? ForToNumber(string? to) => Opt.Numbers.FirstOrDefault(n => Digits(n.From) == Digits(to) && Digits(to).Length > 0);

    public bool IsConfigured(string companyKey) => Opt.AccountSid.Length > 0 && Opt.AuthToken.Length > 0 && ForCompany(companyKey) is not null;

    /// <summary>The URL Twilio signed: the configured public address plus the webhook path, falling back to what this request looked like.</summary>
    public string SignedUrl(string path, string fallbackUrl) => Opt.PublicBaseUrl.Length > 0 ? Opt.PublicBaseUrl.TrimEnd('/') + path : fallbackUrl;

    /// <summary>X-Twilio-Signature = Base64(HMAC-SHA1(AuthToken, url + each POST field name and value, sorted by name)).</summary>
    public bool IsValidSignature(string url, IEnumerable<KeyValuePair<string, string>> form, string? header)
    {
        if (Opt.AuthToken.Length == 0 || string.IsNullOrEmpty(header)) return false;
        var sb = new StringBuilder(url);
        foreach (var kv in form.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(kv.Value);
        var mac = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Opt.AuthToken), Encoding.UTF8.GetBytes(sb.ToString())));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(header.Trim()));
    }

    public Task SendTextAsync(string companyKey, string toDigits, string text, CancellationToken ct) => TrySendTextAsync(companyKey, toDigits, text, ct);

    public async Task<bool> TrySendTextAsync(string companyKey, string toDigits, string text, CancellationToken ct)
    {
        var cfg = ForCompany(companyKey);
        if (cfg is null || Opt.AccountSid.Length == 0) { log.LogWarning("Twilio is not configured for company {Company}", companyKey); return false; }
        var allOk = true;
        // WhatsApp via Twilio caps a message at 1600 characters; split on line breaks when longer.
        foreach (var chunk in Chunk(text, 1500))
            allOk &= await PostAsync(cfg, toDigits, new Dictionary<string, string> { ["Body"] = chunk }, ct);
        return allOk;
    }

    public bool HasTemplate(string companyKey, string templateKey) =>
        IsConfigured(companyKey) && Opt.Templates.TryGetValue(templateKey, out var sid) && sid.StartsWith("HX", StringComparison.Ordinal);

    public async Task<bool> TrySendTemplateAsync(string companyKey, string toDigits, string templateKey, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        var cfg = ForCompany(companyKey);
        if (cfg is null || !HasTemplate(companyKey, templateKey)) return false;
        var vars = System.Text.Json.JsonSerializer.Serialize(variables);
        return await PostAsync(cfg, toDigits, new Dictionary<string, string> { ["ContentSid"] = Opt.Templates[templateKey], ["ContentVariables"] = vars }, ct);
    }

    private async Task<bool> PostAsync(TwilioNumberConfig cfg, string toDigits, Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            fields["From"] = "whatsapp:+" + Digits(cfg.From); fields["To"] = "whatsapp:+" + Digits(toDigits);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{Opt.AccountSid}/Messages.json") { Content = new FormUrlEncodedContent(fields) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Opt.AccountSid}:{Opt.AuthToken}")));
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) log.LogWarning("Twilio send failed: {Status} {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { log.LogWarning(ex, "Twilio send failed"); return false; }
    }

    public static IEnumerable<string> Chunk(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length + line.Length + 1 > max && current.Length > 0) { yield return current.ToString().TrimEnd(); current.Clear(); }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) yield return current.ToString().TrimEnd();
    }
}
