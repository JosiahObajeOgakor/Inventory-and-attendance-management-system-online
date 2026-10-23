namespace Inventory.Domain.Entities;

/// <summary>
/// A trade application being filled in step by step on the website. Every step is saved here, so the applicant can stop and carry on later: on the same
/// device straight away (by its device token), on any device after confirming their email with a code. Once submitted it becomes a <see cref="TradePartner"/>
/// application in each chosen store and this draft is removed. A draft left untouched is followed up by email at 3, 7 and 21 days, then removed 24 hours
/// after the last (warning) email, which frees the email address to start again.
/// </summary>
public class TradeApplicationDraft
{
    public int Id { get; set; }
    /// <summary>Lower-cased. One draft per email address.</summary>
    public string Email { get; set; } = "";
    public DateTime? EmailVerifiedAt { get; set; }
    /// <summary>SHA-256 of the random token kept on the applicant's device; the token itself is never stored.</summary>
    public string DeviceTokenHash { get; set; } = "";
    /// <summary>1 email, 2 business, 3 contact, 4 registration, 5 documents, 6 review.</summary>
    public int Step { get; set; } = 1;
    /// <summary>The answers so far, as JSON (field name → value).</summary>
    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    /// <summary>The last time the applicant did something; follow-ups count from here.</summary>
    public DateTime LastActivityAt { get; set; }
    /// <summary>How many follow-up emails have gone out since the last activity (0–3; the third is the warning).</summary>
    public int RemindersSent { get; set; }
    public DateTime? LastReminderAt { get; set; }
    /// <summary>When the final warning was sent; with no activity 24 hours later the draft is deleted.</summary>
    public DateTime? WarnedAt { get; set; }
    public List<TradeApplicationFile> Files { get; set; } = [];
}

public class TradeApplicationFile
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    /// <summary>"cac", "id" or "other".</summary>
    public string Kind { get; set; } = "cac";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTime UploadedAt { get; set; }
}

public static class EmailCodePurposes
{
    /// <summary>Confirming the email of a new or resumed application.</summary>
    public const string Verify = "verify";
    /// <summary>Signing an approved partner in to their dashboard.</summary>
    public const string Login = "login";
}

/// <summary>A 6-digit code emailed to prove someone owns an address. Only its hash is kept; it expires in 15 minutes and allows 5 tries.</summary>
public class EmailCode
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string Purpose { get; set; } = EmailCodePurposes.Verify;
    public string CodeHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? UsedAt { get; set; }
}
