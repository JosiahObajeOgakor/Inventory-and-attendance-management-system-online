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
/// <param name="AdminOnly">Only the admin hears (no clerk copy): e.g. something waiting for the admin's approval.</param>
public sealed record StaffAlert(string Subject, string Text, string? Html = null, bool AdminOnly = false);

/// <summary>A message to a partner: WhatsApp when their chat is open, SMS, and email. Each channel is tried on its own.</summary>
public sealed record ContactAlert(string Name, string Phone, string? Email, string Subject, string Text, string? ButtonText = null, string? ButtonUrl = null);

public interface IStaffAlerts
{
    Task TellStaffAsync(StaffAlert alert, CancellationToken ct);
    Task TellContactAsync(ContactAlert alert, CancellationToken ct);
}

/// <summary>Runs work inside another store's database and services (one schema per store), e.g. submitting one application to both stores.</summary>
public interface ICompanyScopes
{
    /// <summary>The stores trade partners can apply to (Partners:Stores, default "chewypets,candid"), in order; the first holds the drafts.</summary>
    IReadOnlyList<string> TradeStores { get; }
    string DisplayName(string key);
    Task<T> InAsync<T>(string key, Func<IServiceProvider, Task<T>> work);
}

/// <param name="Stores">The store(s) the email is about; its logo heads the email (Candid Purrfect's when both are involved).</param>
/// <param name="Code">A one-time code shown large in its own box.</param>
public sealed record BrandedMail(string To, string? ToName, string Subject, string Heading, IReadOnlyList<string> Paragraphs, IReadOnlyList<string> Stores,
    string? Code = null, string? ButtonText = null, string? ButtonUrl = null, string? Footnote = null);

/// <summary>Customer-facing email in the business's own look: logo, brand colour, one clear action, contact details in the footer.</summary>
public interface IBrandedEmail
{
    bool IsConfigured { get; }
    Task SendAsync(BrandedMail mail, CancellationToken ct);
}
