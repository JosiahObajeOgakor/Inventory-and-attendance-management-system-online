namespace Inventory.Domain.Entities;

public static class SiteReviewStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>A review a customer left on the website. Shown only after staff approve it, and only because the customer agreed it may be published.</summary>
public class SiteReview
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Where they are, if they said ("Ikeja"). Optional.</summary>
    public string? Place { get; set; }
    public int Rating { get; set; }
    public string Text { get; set; } = "";
    /// <summary>The customer ticked "you may publish this review with my first name".</summary>
    public bool ConsentToPublish { get; set; }
    public string Status { get; set; } = SiteReviewStatuses.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>A partner (vet clinic, retailer, supplier) whose logo the business has permission to show on its website.</summary>
public class SitePartner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Logo { get; set; } = [];
    /// <summary>Ticked by staff: the partner gave permission for their logo to appear.</summary>
    public bool PermissionGiven { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime UpdatedAt { get; set; }
}