using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Payments;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Payments;

public sealed class AlatPayOptions
{
    public const string Section = "AlatPay";
    /// <summary>The secret (subscription) key sent as Ocp-Apim-Subscription-Key. Environment only (AlatPay__SecretKey).</summary>
    public string SecretKey { get; set; } = "";
    /// <summary>Signs webhooks (HMAC-SHA256, Base64, header x-signature). Environment only (AlatPay__WebhookSecret).</summary>
    public string WebhookSecret { get; set; } = "";
    public string BusinessId { get; set; } = "";
    /// <summary>Only for AlatPay's in-browser popup; the server's payment links use <see cref="SecretKey"/>. Kept so both halves live in one place.</summary>
    public string PublicKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://apibox.alatpay.ng";
    /// <summary>Where the customer's browser lands after paying (display only — never trusted to mark anything paid).</summary>
    public string RedirectUrl { get; set; } = "";
    /// <summary>The payment-link API takes currency as a number, not "NGN". Confirm against the sandbox when first set up.</summary>
    public int CurrencyCode { get; set; } = 2;
    /// <summary>Older name for <see cref="SecretKey"/> (AlatPay__SubscriptionKey); used only when SecretKey is empty.</summary>
    public string SubscriptionKey { get; set; } = "";
    /// <summary>The key actually sent to AlatPay.</summary>
    public string Key => SecretKey.Length > 0 ? SecretKey : SubscriptionKey;
}

/// <summary>
/// AlatPay's hosted Payment Link rail: AlatPay serves the payment page (card, bank transfer, USSD) at paylink.alatpay.ng and issues its own reference.
/// Create: POST /merchant-onboarding/api/v1/payment/initialize. Confirm: GET /merchant-onboarding/api/v1/payment/status/{paymentReference}.
/// Like Paystack, a webhook is only believed after the status endpoint confirms the payment.
/// </summary>
public sealed class AlatPayGateway(HttpClient http, IOptions<AlatPayOptions> options, IOptions<NotifyOptions> notify, ILogger<AlatPayGateway> log) : IPaymentGateway
{
    private AlatPayOptions Opt => options.Value;
    public string Provider => PaymentProviders.AlatPay;
    public bool IsConfigured => Opt.SecretKey.Length > 0;
    public string FallbackEmail => string.IsNullOrWhiteSpace(notify.Value.AdminEmail) ? "payments@invalid.example" : notify.Value.AdminEmail;

    private HttpRequestMessage Req(HttpMethod m, string path, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, Opt.BaseUrl.TrimEnd('/') + path) { Content = body };
        r.Headers.Add("Ocp-Apim-Subscription-Key", Opt.SecretKey);
        return r;
    }

    public async Task<GatewayCharge> InitializeAsync(string email, long amountKobo, string reference, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessRuleException("AlatPay isn't set up: the server has no AlatPay key.");
        var body = new JsonObject { ["email"] = email, ["amount"] = amountKobo / 100m, ["currency"] = Opt.CurrencyCode };
        // AlatPay's payment-link API documents only email, amount, currency and redirectUrl, so nothing else is sent (the business is recorded on
        // our side: each business's links live in its own database, and the admin alert names it).
        var returnUrl = metadata.GetValueOrDefault(Inventory.Application.Payments.PaymentLinkService.ReturnUrlKey);
        var redirect = !string.IsNullOrWhiteSpace(returnUrl) ? returnUrl : Opt.RedirectUrl;
        if (!string.IsNullOrWhiteSpace(redirect)) body["redirectUrl"] = redirect;
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Post, "/merchant-onboarding/api/v1/payment/initialize", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("AlatPay initialize failed: {Status}", (int)res.StatusCode); throw new BusinessRuleException("Couldn't create the AlatPay payment link right now. Please try again."); }
            var d = Prop(JsonNode.Parse(text), "data");
            var url = Str(d, "paymentUrl");
            var providerRef = Str(d, "paymentReference");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(providerRef)) throw new BusinessRuleException("Couldn't create the AlatPay payment link right now. Please try again.");
            return new GatewayCharge(url, providerRef);
        }
        catch (HttpRequestException) { throw new BusinessRuleException("Couldn't reach AlatPay. Check the server's internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BusinessRuleException("AlatPay took too long to answer. Please try again."); }
        catch (System.Text.Json.JsonException) { throw new BusinessRuleException("AlatPay sent an answer we couldn't read. Please try again."); }
    }

    public async Task<GatewayVerification?> VerifyAsync(string providerReference, CancellationToken ct)
    {
        if (!IsConfigured || providerReference.Length is 0 or > 100 || providerReference.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return null;
        try
        {
            using var res = await http.SendAsync(Req(HttpMethod.Get, "/merchant-onboarding/api/v1/payment/status/" + Uri.EscapeDataString(providerReference)), ct);
            if (!res.IsSuccessStatusCode) return null;
            return ParseStatus(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            log.LogWarning("AlatPay verify failed");
            return null;
        }
    }

    /// <summary>Reads the status response. Paid only when the link AND its transaction both say "completed". Amounts come back in naira.</summary>
    public static GatewayVerification? ParseStatus(string json)
    {
        var d = Prop(JsonNode.Parse(json), "data");
        if (d is null) return null;
        var tx = Prop(d, "transactions");
        if (tx is JsonArray arr) tx = arr.LastOrDefault(t => Completed(Str(t, "status"))) ?? arr.LastOrDefault();
        var paid = Completed(Str(d, "status")) && (tx is null || Completed(Str(tx, "status")));
        var amount = Num(tx, "amount") ?? Num(d, "amount") ?? 0m;
        var currency = Str(tx, "currency") ?? Str(d, "currency") ?? "";
        return new GatewayVerification(paid, (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), currency, Str(tx, "channel"));
    }

    private static bool Completed(string? s) => string.Equals(s, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>AlatPay signs the raw body: Base64(HMAC-SHA256(body, webhook secret)) in the x-signature header.</summary>
    public bool IsValidSignature(string rawBody, string? signature) => IsValidSignature(Opt.WebhookSecret, rawBody, signature);

    public static bool IsValidSignature(string secret, string rawBody, string? signature)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature)) return false;
        var mac = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(signature.Trim()));
    }

    // AlatPay mixes camelCase (REST) and PascalCase (webhooks), so look names up case-insensitively.
    private static JsonNode? Prop(JsonNode? n, string name) =>
        n is JsonObject o ? o.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static string? Str(JsonNode? n, string name) => Prop(n, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal? Num(JsonNode? n, string name) => Prop(n, name) switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}

/// <summary>
/// AlatPay "bank transfer" rail: a single-use virtual account per chat order, shown inside WhatsApp. Uses the same AlatPay key as the payment links.
/// The money arriving is confirmed twice: the signed webhook says "look", and <see cref="VerifyAsync"/> asks AlatPay itself before anything is recorded.
/// </summary>
public sealed class AlatPayVirtualAccounts(HttpClient http, IOptions<AlatPayOptions> options, IClock clock, ILogger<AlatPayVirtualAccounts> log) : IVirtualAccountGateway
{
    private AlatPayOptions Opt => options.Value;
    public bool IsConfigured => Opt.Key.Length > 0 && Opt.BusinessId.Length > 0;

    private HttpRequestMessage Req(HttpMethod m, string path, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, Opt.BaseUrl.TrimEnd('/') + path) { Content = body };
        r.Headers.Add("Ocp-Apim-Subscription-Key", Opt.Key);
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

    /// <summary>Same signature rule as the payment links: Base64(HMAC-SHA256(body, webhook secret)) in x-signature.</summary>
    public bool IsValidSignature(string rawBody, string? signature) => AlatPayGateway.IsValidSignature(Opt.WebhookSecret, rawBody, signature);
}
