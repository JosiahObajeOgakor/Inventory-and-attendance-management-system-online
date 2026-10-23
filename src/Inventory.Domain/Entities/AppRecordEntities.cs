namespace Inventory.Domain.Entities;

/// <summary>
/// One row of any record-keeping app (Sign, Rental, Events, Fleet…): which app and kind it belongs to, the few fields every list needs (title, status, date,
/// amount, a public code), and everything else as JSON checked against the app's definition (Inventory.Application.AppsEngine). Child rows (an event's
/// registrations, a course's lessons) point at their parent with <see cref="ParentId"/>.
/// </summary>
public class AppRecord
{
    public int Id { get; set; }
    public string App { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? ParentId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    /// <summary>A public handle: a blog post's slug, a signing link's token, an event's slug. Unique within its app and kind.</summary>
    public string? Code { get; set; }
    public DateOnly? Date { get; set; }
    public decimal? Amount { get; set; }
    /// <summary>The kind's other fields, as a JSON object.</summary>
    public string Data { get; set; } = "{}";
    public int CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class RecipientStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    /// <summary>Not sent on purpose (opted out, channel not set up, no way to unsubscribe): the reason is on the row.</summary>
    public const string Skipped = "Skipped";
    /// <summary>Claimed by a sender, so two servers never send the same message.</summary>
    public const string Sending = "Sending";
}

/// <summary>One message to one person from a campaign or an automation rule: written when the campaign is sent, delivered by the queue, and kept as the record of who was told what.</summary>
public class CampaignRecipient
{
    public long Id { get; set; }
    /// <summary>The campaign or rule (an <see cref="AppRecord"/>) it belongs to.</summary>
    public int RecordId { get; set; }
    /// <summary>Email, SMS or WhatsApp.</summary>
    /// <summary>The customer it is for; consent is checked again at the moment of sending.</summary>
    public int? CustomerId { get; set; }
    public string Channel { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Name { get; set; }
    public string? Subject { get; set; }
    public string Message { get; set; } = "";
    public string Status { get; set; } = RecipientStatuses.Pending;
    public string? Error { get; set; }
    /// <summary>Stops the same person being sent the same thing twice by the same rule (e.g. "customer-12:2026-10"). Null for one-off campaigns.</summary>
    public string? DedupeKey { get; set; }
    /// <summary>About the customer's own order (an unpaid payment, a cancelled order), not an advert: it may go to a customer who never answered the opt-in question, but never to one who opted out.</summary>
    public bool IsService { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
