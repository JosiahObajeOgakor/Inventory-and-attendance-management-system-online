using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Messaging;
using Inventory.Infrastructure.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Messaging;

/// <summary>Customer SMS over Termii, using the same account settings as the admin payment alerts (Notify:SmsApiKey, SmsSenderId, SmsBaseUrl).</summary>
public sealed class TermiiSmsSender(IOptions<NotifyOptions> options, IHttpClientFactory httpFactory, ILogger<TermiiSmsSender> log) : ISmsSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.SmsApiKey);

    public async Task<bool> TrySendAsync(string toDigits, string text, CancellationToken ct)
    {
        var o = options.Value;
        if (!IsConfigured) return false;
        try
        {
            var http = httpFactory.CreateClient("sms");
            var payload = new JsonObject { ["to"] = toDigits, ["from"] = o.SmsSenderId, ["sms"] = text.Length > 600 ? text[..600] : text, ["type"] = "plain", ["channel"] = "generic", ["api_key"] = o.SmsApiKey };
            using var res = await http.PostAsync(o.SmsBaseUrl.TrimEnd('/') + "/api/sms/send", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode) log.LogWarning("SMS provider answered {Status}", (int)res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { log.LogWarning("SMS to a customer failed: {Message}", ex.Message); return false; }
    }
}
