namespace Inventory.Application.Messaging;

/// <summary>A tappable reply button. <see cref="Id"/> comes back in the webhook when tapped; <see cref="Title"/> is at most 20 characters.</summary>
public sealed record WhatsAppButton(string Id, string Title);

/// <summary>Sends WhatsApp messages for one company. Implemented over Meta's Cloud API in Infrastructure.
/// Kept in Application (not referencing Infrastructure) so Payments.cs can depend on it without a back-reference.</summary>
public interface IWhatsAppSender
{
    bool IsConfigured(string companyKey);
    /// <summary>Free-form text. Meta only delivers it within 24 hours of the recipient's last message to us.</summary>
    Task SendTextAsync(string companyKey, string toE164, string text, CancellationToken ct);
    Task SendDocumentAsync(string companyKey, string toE164, byte[] pdf, string filename, string caption, CancellationToken ct);
    /// <summary>Up to three reply buttons under a message (same 24-hour rule as text).</summary>
    Task SendButtonsAsync(string companyKey, string toE164, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct);
    /// <summary>A pre-approved template — the only way to message someone (admin, rider) who hasn't written to us in the last 24 hours.
    /// Returns false when Meta refuses it (e.g. the template isn't approved yet).</summary>
    Task<bool> SendTemplateAsync(string companyKey, string toE164, string templateName, string languageCode, IReadOnlyList<string> bodyParameters, CancellationToken ct);
}
