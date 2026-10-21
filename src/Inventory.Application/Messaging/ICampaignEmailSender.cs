namespace Inventory.Application.Messaging;

/// <summary>Campaign email: SendGrid when configured (Twilio's email product), otherwise the existing SMTP mailbox. Always carries an unsubscribe link and header.</summary>
public interface ICampaignEmailSender
{
    bool IsConfigured { get; }
    Task<bool> TrySendAsync(string toEmail, string? toName, string fromName, string subject, string html, string text, string unsubscribeUrl, CancellationToken ct);
}
