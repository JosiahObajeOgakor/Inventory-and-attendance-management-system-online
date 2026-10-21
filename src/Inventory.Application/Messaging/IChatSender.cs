namespace Inventory.Application.Messaging;

/// <summary>Pushes a text into a customer's WhatsApp chat when no inbound message is waiting for a reply (e.g. "payment received" after the bank webhook, or a campaign). Implemented over Twilio in Infrastructure.</summary>
public interface IChatSender
{
    bool IsConfigured(string companyKey);
    /// <param name="toDigits">Customer number, digits only with country code (2348012345678).</param>
    Task SendTextAsync(string companyKey, string toDigits, string text, CancellationToken ct);
    /// <summary>Same, but tells the caller whether Twilio accepted it (campaigns log the outcome).</summary>
    Task<bool> TrySendTextAsync(string companyKey, string toDigits, string text, CancellationToken ct);
    /// <summary>Is an approved WhatsApp template configured under this key (Twilio:Templates:{key})? Needed to message anyone outside the 24-hour window.</summary>
    bool HasTemplate(string companyKey, string templateKey);
    /// <summary>Sends an approved template. Variables are numbered "1", "2", … as in the template body. Returns false when no template is configured or Twilio refused it.</summary>
    Task<bool> TrySendTemplateAsync(string companyKey, string toDigits, string templateKey, IReadOnlyDictionary<string, string> variables, CancellationToken ct);
}
