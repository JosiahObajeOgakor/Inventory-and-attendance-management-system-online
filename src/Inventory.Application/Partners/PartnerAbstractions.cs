namespace Inventory.Application.Partners;

/// <summary>What the outside registry said about a business registration number.</summary>
public sealed record VerificationResult(string Status, string? RegisteredName, string? Note);

/// <summary>
/// Checks a CAC registration number with an outside KYB provider (Dojah when Verification:Provider is "dojah"). With no provider set up every
/// application is marked NotChecked and staff look at the uploaded certificate themselves; a provider outage never blocks an application.
/// </summary>
public interface IBusinessVerifier
{
    bool IsConfigured { get; }
    Task<VerificationResult> VerifyAsync(string cacNumber, string businessName, CancellationToken ct);
}

/// <summary>A message for the people who run the business: the admin on WhatsApp, SMS and email, the clerks on email only (a copy).</summary>
public sealed record StaffAlert(string Subject, string Text, string? Html = null);

/// <summary>A message to a partner: WhatsApp when their chat is open, SMS, and email. Each channel is tried on its own.</summary>
public sealed record ContactAlert(string Name, string Phone, string? Email, string Subject, string Text);

public interface IStaffAlerts
{
    Task TellStaffAsync(StaffAlert alert, CancellationToken ct);
    Task TellContactAsync(ContactAlert alert, CancellationToken ct);
}
