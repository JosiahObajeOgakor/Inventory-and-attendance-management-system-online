using System.Net.Http.Headers;
using System.Text;
using Inventory.Application.Messaging;
using Inventory.Infrastructure.WhatsApp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Messaging;

/// <summary>
/// SMS over the same Twilio account as WhatsApp. Sends from Twilio:SmsFrom (an SMS-capable Twilio number or an approved alphanumeric sender ID such as
/// "ChewyPets") or through Twilio:SmsMessagingServiceSid (MG…). When neither is set, SMS falls back to Termii, and if that isn't set either, no SMS is sent.
/// </summary>
public sealed class TwilioSmsSender(HttpClient http, IOptions<TwilioOptions> options, TermiiSmsSender termii, ILogger<TwilioSmsSender> log) : ISmsSender
{
    private TwilioOptions Opt => options.Value;
    private bool TwilioReady => Opt.AccountSid.Length > 0 && Opt.AuthToken.Length > 0 && (Opt.SmsFrom.Length > 0 || Opt.SmsMessagingServiceSid.StartsWith("MG", StringComparison.Ordinal));

    public bool IsConfigured => TwilioReady || termii.IsConfigured;

    public async Task<bool> TrySendAsync(string toDigits, string text, CancellationToken ct)
    {
        if (!TwilioReady) return await termii.TrySendAsync(toDigits, text, ct);
        try
        {
            var fields = new Dictionary<string, string> { ["To"] = "+" + TwilioWhatsAppClient.Digits(toDigits), ["Body"] = text.Length > 1500 ? text[..1500] : text };
            if (Opt.SmsMessagingServiceSid.StartsWith("MG", StringComparison.Ordinal)) fields["MessagingServiceSid"] = Opt.SmsMessagingServiceSid;
            else fields["From"] = Opt.SmsFrom.Any(char.IsLetter) ? Opt.SmsFrom : "+" + TwilioWhatsAppClient.Digits(Opt.SmsFrom);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{Opt.AccountSid}/Messages.json") { Content = new FormUrlEncodedContent(fields) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Opt.AccountSid}:{Opt.AuthToken}")));
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) log.LogWarning("Twilio SMS failed: {Status} {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { log.LogWarning("Twilio SMS failed: {Message}", ex.Message); return false; }
    }
}
