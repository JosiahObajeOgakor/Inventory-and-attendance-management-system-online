namespace Inventory.Domain.Entities;

/// <summary>Whether a customer agreed (in the WhatsApp chat, or by signing up) to receive reminders and offers. Nothing is ever sent to anyone who is not <see cref="OptedIn"/>.</summary>
public static class MarketingStatuses
{
    public const string Unknown = "Unknown";
    public const string OptedIn = "OptedIn";
    public const string OptedOut = "OptedOut";
}

public static class CampaignTypes
{
    /// <summary>"You bought X on …, it is probably finishing" — built from the customer's own purchase history and today's stock.</summary>
    public const string Reorder = "Reorder";
    /// <summary>Orders started in chat and never paid, and balances past their due date.</summary>
    public const string UnpaidOrder = "UnpaidOrder";
    /// <summary>A product that is in stock and worth moving, matched to what the customer has bought before.</summary>
    public const string Promo = "Promo";
    /// <summary>Tells the admin which products will run out within the lead-time window and what to order.</summary>
    public const string AdminRestock = "AdminRestock";
    /// <summary>People who picked products in the chat and left before ordering: reminds them of exactly what they picked.</summary>
    public const string ChatDropOff = "ChatDropOff";
    public static readonly string[] All = [Reorder, UnpaidOrder, ChatDropOff, Promo, AdminRestock];
}

/// <summary>One kind of automatic message, switched on/off and scheduled by an admin. Messages are drafted fresh each run from live records; no per-message approval.</summary>
public class Campaign
{
    public int Id { get; set; }
    public string Type { get; set; } = CampaignTypes.Reorder;
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool SendWhatsApp { get; set; } = true;
    public bool SendEmail { get; set; }
    /// <summary>Time of day to run, Lagos time, as minutes after midnight (450 = 07:30).</summary>
    public int RunMinute { get; set; } = 450;
    /// <summary>Run every N days (1 = daily, 7 = weekly), counted from <see cref="StartDate"/>.</summary>
    public int EveryNDays { get; set; } = 1;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    /// <summary>Do not send a customer more than one marketing message in this many days, across all campaigns.</summary>
    public int CooldownDays { get; set; } = 3;
    /// <summary>Most customers messaged per run — a safety cap.</summary>
    public int MaxRecipients { get; set; } = 100;
    /// <summary>Reorder: when a customer has no purchase rhythm yet, remind this many days after their last purchase. AdminRestock/Reorder: "about to run out" means within this many days of cover.</summary>
    public int ThresholdDays { get; set; } = 7;
    public int FallbackRepurchaseDays { get; set; } = 21;
    /// <summary>The date a run last started — claimed with a single UPDATE so two servers (or a restart) never run it twice in a day.</summary>
    public DateOnly? LastRunOn { get; set; }
    public DateTime? LastRunAt { get; set; }
    public int LastRunSent { get; set; }
    public string? LastRunNote { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public static class CampaignMessageStatuses
{
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>Every message a campaign sent (or was prevented from sending, and why). The audit trail for "why did this customer get that text?".</summary>
public class CampaignMessage
{
    public long Id { get; set; }
    public int CampaignId { get; set; }
    public int? CustomerId { get; set; }
    public string Recipient { get; set; } = "";
    public string Channel { get; set; } = "WhatsApp";
    public string Language { get; set; } = ChatLanguages.English;
    public string Text { get; set; } = "";
    public string Status { get; set; } = CampaignMessageStatuses.Sent;
    public string? Detail { get; set; }
    /// <summary>"template" when the 24-hour window was closed and an approved template carried the text; "text" for free text; "email".</summary>
    public string? Mode { get; set; }
    public DateTime CreatedAt { get; set; }
}
