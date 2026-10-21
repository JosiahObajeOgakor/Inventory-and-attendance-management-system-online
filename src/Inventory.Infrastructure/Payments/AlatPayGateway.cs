using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Payments;

public sealed class AlatPayOptions
{
    public const string Section = "AlatPay";
    /// <summary>Ocp-Apim-Subscription-Key from the AlatPay dashboard. Environment only (AlatPay__SubscriptionKey).</summary>
    public string SubscriptionKey { get; set; } = "";
    /// <summary>The business id (a GUID) shown in the AlatPay dashboard.</summary>
    public string BusinessId { get; set; } = "";
    /// <summary>The webhook secret from the dashboard; webhooks are HMAC-SHA256 signed with it.</summary>
    public string WebhookSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "https://apibox.alatpay.ng";
}

/// <summary>
/// AlatPay "bank transfer" rail over HTTPS: a single-use virtual account per order that is shown inside the chat. The money arriving is confirmed
/// twice — the signed webhook says "look", and <see cref="VerifyAsync"/> asks AlatPay itself before anything is recorded.
/// </summary>
public sealed class AlatPayGateway(HttpClient http, IOptions<AlatPayOptions> options, IClock clock, ILogger<AlatPayGateway> log) : IVirtualAccountGateway
{
    private AlatPayOptions Opt => options.Value;
    public bool IsConfigured => Opt.SubscriptionKey.Length > 0 && Opt.BusinessId.Length > 0;

    private HttpRequestMessage Req(HttpMethod m, string path, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, Opt.BaseUrl.TrimEnd('/') + path) { Content = body };
        r.Headers.Add("Ocp-Apim-Subscription-Key", Opt.SubscriptionKey);
        return r;
    }

    public async Task<VirtualAccount> CreateAsync(string orderId, decimal amount, string description, string firstName, string lastName, string email, string? phone, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessRuleException("Online payments aren't set up: the server has no AlatPay key.");
        var body = new JsonObject
        {
            ["businessId"] = Opt.BusinessId, ["amount"] = amount, ["currency"] = "NGN", ["orderId"] = orderId,
            ["description"] = description.Length > 100 ? description[..100] : description,
            ["customer"] = new JsonObject { ["email"] = email, ["phone"] = phone ?? "", ["firstName"] = firstName, ["lastName"] = lastName, ["metadata"] = "" },
        };
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, "/bank-transfer/api/v1/bankTransfer/virtualAccount", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("AlatPay virtual account failed: {Status} {Body}", (int)res.StatusCode, text.Length > 300 ? text[..300] : text); throw new BusinessRuleException("Couldn't create the payment account right now. Please try again."); }
            var d = JsonNode.Parse(text)?["data"];
            var number = d?["virtualBankAccountNumber"]?.ToString();
            var txId = d?["transactionId"]?.ToString() ?? d?["id"]?.ToString();
            if (string.IsNullOrEmpty(number) || string.IsNullOrEmpty(txId)) throw new BusinessRuleException("Couldn't create the payment account right now. Please try again.");
            var expires = DateTime.TryParse(d?["expiredAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var e)
                ? e : clock.UtcNow.AddMinutes(30);   // the two AlatPay docs disagree (30 min vs 24 h); without an explicit expiry assume the short one
            return new VirtualAccount(number, BankName(d?["virtualBankCode"]?.ToString()), txId, expires);
        }
        catch (HttpRequestException) { throw new BusinessRuleException("Couldn't reach AlatPay. Check the server's internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BusinessRuleException("AlatPay took too long to answer. Please try again."); }
        catch (System.Text.Json.JsonException) { throw new BusinessRuleException("Couldn't create the payment account right now. Please try again."); }
    }

    public async Task<TransferStatus?> VerifyAsync(string transactionId, CancellationToken ct)
    {
        if (!IsConfigured || transactionId.Length is 0 or > 80 || transactionId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return null;
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Get, "/bank-transfer/api/v1/bankTransfer/transactions/" + Uri.EscapeDataString(transactionId)), ct);
            if (!res.IsSuccessStatusCode) return null;
            var d = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"];
            if (d is null) return null;
            var status = d["status"]?.ToString();
            var completed = status is not null && CompletedStatuses.Contains(status.Trim().ToLowerInvariant());
            decimal.TryParse(d["amount"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount);
            decimal? sent = decimal.TryParse(d["amountSent"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null;
            return new TransferStatus(completed, amount, sent, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { log.LogWarning("AlatPay verify failed"); return null; }
    }

    private static readonly HashSet<string> CompletedStatuses = ["completed", "successful", "success", "paid"];

    private static string BankName(string? code) => code switch { "035" or null or "" => "Wema Bank", "058" => "GTBank", "044" => "Access Bank", "011" => "First Bank", _ => $"Bank {code}" };

    /// <summary>AlatPay signs the raw request body: HMAC-SHA256 with the webhook secret, Base64-encoded, in the x-signature header.</summary>
    public bool IsValidSignature(string rawBody, string? signature)
    {
        if (Opt.WebhookSecret.Length == 0) { log.LogError("AlatPay webhook rejected: AlatPay__WebhookSecret is not set on this server."); return false; }
        if (string.IsNullOrEmpty(signature)) { log.LogWarning("AlatPay webhook rejected: no x-signature header."); return false; }
        var mac = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Opt.WebhookSecret), Encoding.UTF8.GetBytes(rawBody)));
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(signature.Trim()));
        // Only lengths and a 4-character prefix of each side are logged (an HMAC prefix reveals nothing about the secret): enough to tell a wrong secret from a re-encoded body.
        if (!ok) log.LogWarning("AlatPay webhook signature mismatch: body {Bytes} bytes, received {GotLen} chars starting {Got}, expected {WantLen} chars starting {Want}, secret length {SecretLen}.",
            Encoding.UTF8.GetByteCount(rawBody), signature.Trim().Length, signature.Trim()[..Math.Min(4, signature.Trim().Length)], mac.Length, mac[..4], Opt.WebhookSecret.Length);
        return ok;
    }
}
