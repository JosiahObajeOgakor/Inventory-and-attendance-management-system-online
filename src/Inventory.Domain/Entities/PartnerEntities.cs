namespace Inventory.Domain.Entities;

public static class PartnerStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Suspended = "Suspended";
    public static readonly string[] All = [Pending, Approved, Rejected, Suspended];
}

/// <summary>What the outside check of the business registration (CAC) and tax number found.</summary>
public static class PartnerVerifications
{
    /// <summary>No verification provider is set up, or the check hasn't run: staff check the documents by hand.</summary>
    public const string NotChecked = "NotChecked";
    public const string Verified = "Verified";
    /// <summary>The registry knows the number but under a different name: look closely before approving.</summary>
    public const string Mismatch = "Mismatch";
    public const string NotFound = "NotFound";
    /// <summary>The provider could not be reached; try again later.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// A pet shop, wholesaler or distributor that applied to buy at trade prices. Until staff approve it, the number behind it is quoted retail prices
/// like anyone else; once approved, its contacts see the tier staff chose (which can differ from the one they asked for). Suspending takes the prices
/// and the portal away at once without losing the history.
/// </summary>
public class TradePartner
{
    public int Id { get; set; }
    public string BusinessName { get; set; } = "";
    public string ContactName { get; set; } = "";
    /// <summary>International digits (2348031234567), the same form the chat uses, so a WhatsApp message from this number is recognised.</summary>
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>CAC registration number (RC… for companies, BN… for business names).</summary>
    public string CacNumber { get; set; } = "";
    public string? TaxId { get; set; }
    /// <summary>The applicant's own words about the business: what they sell, where, how much a month.</summary>
    public string Description { get; set; } = "";
    /// <summary><see cref="PriceTiers.Wholesaler"/> or <see cref="PriceTiers.Distributor"/>.</summary>
    public string RequestedTier { get; set; } = PriceTiers.Wholesaler;
    /// <summary>Set by staff on approval; the tier the chat, shop and portal price at.</summary>
    public string? ApprovedTier { get; set; }
    public string Status { get; set; } = PartnerStatuses.Pending;
    /// <summary>"website" or "whatsapp".</summary>
    public string Channel { get; set; } = "website";
    public string Verification { get; set; } = PartnerVerifications.NotChecked;
    /// <summary>The name the registry holds for the CAC number, when it was checked.</summary>
    public string? VerifiedName { get; set; }
    public string? VerificationNote { get; set; }
    /// <summary>The customer record their orders are booked to; created or linked on approval.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    /// <summary>The login they use for the partner portal (identity user id), once one is issued.</summary>
    public string? UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    /// <summary>Why it was rejected or suspended; shown to the applicant.</summary>
    public string? DecisionNote { get; set; }
    public List<TradePartnerDocument> Documents { get; set; } = [];
}

/// <summary>A document an applicant uploaded (CAC certificate, shop photo). Kept small: images are compressed in the browser before upload.</summary>
public class TradePartnerDocument
{
    public int Id { get; set; }
    public int TradePartnerId { get; set; }
    /// <summary>"cac", "id" or "other".</summary>
    public string Kind { get; set; } = "cac";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTime UploadedAt { get; set; }
}

public static class WalletEntryKinds
{
    /// <summary>Money paid in (AlatPay link, virtual account or a confirmed bank transfer).</summary>
    public const string Funding = "Funding";
    /// <summary>An order paid from the wallet (negative).</summary>
    public const string Purchase = "Purchase";
    /// <summary>Money put back after a cancelled order.</summary>
    public const string Refund = "Refund";
    /// <summary>A correction made by management, always with a reason.</summary>
    public const string Adjustment = "Adjustment";
}

public static class WalletEntryStatuses
{
    /// <summary>Waiting for the money to be confirmed by the processor or by staff; does not count towards the balance.</summary>
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Failed = "Failed";
}

/// <summary>
/// One movement on a partner's wallet. The balance is the sum of confirmed entries, never a stored number that can drift. Every entry has a unique
/// reference, so a processor callback that arrives twice, or a button pressed twice, can't credit or spend the same money twice.
/// </summary>
public class WalletEntry
{
    public long Id { get; set; }
    public int TradePartnerId { get; set; }
    public string Kind { get; set; } = WalletEntryKinds.Funding;
    /// <summary>Positive in, negative out.</summary>
    public decimal Amount { get; set; }
    public string Status { get; set; } = WalletEntryStatuses.Pending;
    /// <summary>Unique: the payment link reference for a funding, "INV-{id}" for a purchase, the staff-entered transfer reference for a bank transfer.</summary>
    public string Reference { get; set; } = "";
    /// <summary>"alatpay", "transfer", "wallet", "staff".</summary>
    public string Method { get; set; } = "";
    public int? PaymentLinkId { get; set; }
    public int? InvoiceId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmedBy { get; set; }
}
