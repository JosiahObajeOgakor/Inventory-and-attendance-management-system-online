using System.Security.Cryptography;
using System.Text;

namespace Inventory.Application.Campaigns;

public sealed class CampaignOptions
{
    public const string Section = "Campaigns";
    /// <summary>Hard ceiling on AI calls in ONE campaign run. The AI writes a few wording variants per situation and language — never one call per customer.</summary>
    public int MaxAiCallsPerRun { get; set; } = 15;
    /// <summary>Free-text WhatsApp is only allowed this long after the customer's last message; after that an approved template must carry the text.</summary>
    public int WindowHours { get; set; } = 23;
    public bool SchedulerEnabled { get; set; } = true;
    /// <summary>Who gets the restock digest. Falls back to Notify:AdminPhone / Notify:AdminEmail.</summary>
    public string AdminName { get; set; } = "";
    public string AdminPhone { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    /// <summary>Public https address used in email unsubscribe links. Falls back to Twilio:PublicBaseUrl.</summary>
    public string PublicBaseUrl { get; set; } = "";
    /// <summary>Signs unsubscribe links. Falls back to the Twilio auth token. Without any secret, email campaigns are skipped rather than sent without a working unsubscribe.</summary>
    public string SecretKey { get; set; } = "";
}

/// <summary>Unsubscribe links in emails: /api/marketing/unsubscribe?k=company&amp;c=customerId&amp;t=signature. The signature stops anyone unsubscribing other people by guessing ids.</summary>
public static class UnsubscribeLinks
{
    public static string Sign(string secret, string companyKey, int customerId) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"unsub:{companyKey}:{customerId}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool Valid(string secret, string companyKey, int customerId, string? token) =>
        !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(token) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, companyKey, customerId)), Encoding.ASCII.GetBytes(token));

    public static string Url(string baseUrl, string secret, string companyKey, int customerId) =>
        $"{baseUrl.TrimEnd('/')}/api/marketing/unsubscribe?k={Uri.EscapeDataString(companyKey)}&c={customerId}&t={Sign(secret, companyKey, customerId)}";
}
