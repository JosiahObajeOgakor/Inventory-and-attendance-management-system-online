namespace Inventory.Application.Messaging;

/// <summary>Sends WhatsApp messages for one company. Implemented over Meta's Cloud API in Infrastructure.
/// Kept in Application (not referencing Infrastructure) so Payments.cs can depend on it without a back-reference.</summary>
public interface IWhatsAppSender
{
    bool IsConfigured(string companyKey);
    Task SendTextAsync(string companyKey, string toE164, string text, CancellationToken ct);
    Task SendDocumentAsync(string companyKey, string toE164, byte[] pdf, string filename, string caption, CancellationToken ct);
}
