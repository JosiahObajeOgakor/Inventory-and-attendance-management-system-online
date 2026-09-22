using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Application.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Payments;

public sealed class PaystackOptions
{
    public const string Section = "Paystack";
    /// <summary>sk_live_… or sk_test_…  Environment only (Paystack__SecretKey); never a file in the repository.</summary>
    public string SecretKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    /// <summary>Where Paystack sends the customer after paying, e.g. https://stock.example.com/paid. Optional.</summary>
    public string CallbackUrl { get; set; } = "";
}

public sealed class NotifyOptions
{
    public const string Section = "Notify";
    public string AdminEmail { get; set; } = "";
    /// <summary>The number to text when an invoice is paid, e.g. 09150464707.</summary>
    public string AdminPhone { get; set; } = "";
    /// <summary>SMS needs a provider account. Termii is supported; leave the key empty to send email only.</summary>
    public string SmsApiKey { get; set; } = "";
    public string SmsSenderId { get; set; } = "";
    public string SmsBaseUrl { get; set; } = "https://api.ng.termii.com";
}

/// <summary>Paystack over HTTPS. Amounts are in kobo. A webhook is only believed after <see cref="VerifyAsync"/> asks Paystack itself.</summary>
public sealed class PaystackGateway(HttpClient http, IOptions<PaystackOptions> options, IOptions<NotifyOptions> notify, ILogger<PaystackGateway> log) : IPaymentGateway
{
    private PaystackOptions Opt => options.Value;
    public bool IsConfigured => Opt.SecretKey.StartsWith("sk_", StringComparison.Ordinal);
    public string FallbackEmail => string.IsNullOrWhiteSpace(notify.Value.AdminEmail) ? "payments@invalid.example" : notify.Value.AdminEmail;

    private HttpRequestMessage Req(HttpMethod m, string path, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, "https://api.paystack.co" + path) { Content = body };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Opt.SecretKey);
        return r;
    }

    public async Task<GatewayCharge> InitializeAsync(string email, long amountKobo, string reference, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessRuleException("Online payments aren't set up: the server has no Paystack key.");
        var body = new JsonObject
        {
            ["email"] = email, ["amount"] = amountKobo, ["currency"] = "NGN", ["reference"] = reference,
            ["metadata"] = new JsonObject(metadata.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
        if (!string.IsNullOrWhiteSpace(Opt.CallbackUrl)) body["callback_url"] = Opt.CallbackUrl;
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, "/transaction/initialize", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("Paystack initialize failed: {Status}", (int)res.StatusCode); throw new BusinessRuleException("Couldn't create the payment link right now. Please try again."); }
            var d = JsonNode.Parse(text)?["data"];
            var url = d?["authorization_url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(url)) throw new BusinessRuleException("Couldn't create the payment link right now. Please try again.");
            return new GatewayCharge(url, d?["reference"]?.GetValue<string>() ?? reference);
        }
        catch (HttpRequestException) { throw new BusinessRuleException("Couldn't reach Paystack. Check the server's internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BusinessRuleException("Paystack took too long to answer. Please try again."); }
    }

    public async Task<GatewayVerification?> VerifyAsync(string reference, CancellationToken ct)
    {
        if (!IsConfigured || reference.Length is 0 or > 100 || reference.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.' or '=' or '_'))) return null;
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Get, "/transaction/verify/" + Uri.EscapeDataString(reference)), ct);
            if (!res.IsSuccessStatusCode) return null;
            var d = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"];
            if (d is null) return null;
            return new GatewayVerification(d["status"]?.GetValue<string>() == "success", d["amount"]?.GetValue<long>() ?? 0, d["currency"]?.GetValue<string>() ?? "", d["channel"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { log.LogWarning("Paystack verify failed"); return null; }
    }

    /// <summary>Paystack signs the raw request body with HMAC-SHA512 using the secret key and sends it hex-encoded in x-paystack-signature.</summary>
    public bool IsValidSignature(string rawBody, string? signature)
    {
        if (!IsConfigured || string.IsNullOrEmpty(signature)) return false;
        var mac = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(Opt.SecretKey), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}

/// <summary>Tells the admin a payment arrived: an email always, and an SMS when an SMS provider key is configured.</summary>
public sealed class AdminNotifier(IEmailSender email, IOptions<NotifyOptions> options, IHttpClientFactory httpFactory, ILogger<AdminNotifier> log) : IAdminNotifier
{
    public async Task NotifyAsync(string subject, string message, CancellationToken ct)
    {
        var o = options.Value; var failures = 0;
        if (!string.IsNullOrWhiteSpace(o.AdminEmail) && email.IsConfigured)
        {
            try
            {
                var html = $"<p style=\"font:15px Segoe UI,Arial,sans-serif\">{System.Net.WebUtility.HtmlEncode(message)}</p>";
                await email.SendAsync(new EmailMessage(o.AdminEmail, null, subject.Replace('\r', ' ').Replace('\n', ' '), html, message, [], "Payments", null), ct);
            }
            catch (Exception ex) { failures++; log.LogWarning(ex, "Admin email notification failed"); }
        }
        if (!string.IsNullOrWhiteSpace(o.AdminPhone) && !string.IsNullOrWhiteSpace(o.SmsApiKey))
        {
            try
            {
                var http = httpFactory.CreateClient("sms");
                var payload = new JsonObject { ["to"] = International(o.AdminPhone), ["from"] = o.SmsSenderId, ["sms"] = message.Length > 300 ? message[..300] : message, ["type"] = "plain", ["channel"] = "generic", ["api_key"] = o.SmsApiKey };
                using var res = await http.PostAsync(o.SmsBaseUrl.TrimEnd('/') + "/api/sms/send", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                if (!res.IsSuccessStatusCode) { failures++; log.LogWarning("SMS provider answered {Status}", (int)res.StatusCode); }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { failures++; log.LogWarning("SMS notification failed"); }
        }
        else log.LogInformation("No SMS provider key configured; the admin was told by email only.");
        if (failures > 0) throw new InvalidOperationException("A notification channel failed.");
    }

    /// <summary>09150464707 → 2349150464707 (the form SMS providers want).</summary>
    public static string International(string phone)
    {
        var d = new string(phone.Where(char.IsDigit).ToArray());
        return d.StartsWith("234") ? d : d.StartsWith('0') ? "234" + d[1..] : d;
    }
}
