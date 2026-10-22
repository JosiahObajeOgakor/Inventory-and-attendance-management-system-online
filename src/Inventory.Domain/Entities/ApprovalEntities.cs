namespace Inventory.Domain.Entities;

/// <summary>Actions that can be held for a second person's approval. Codes match the permission that performs them.</summary>
public static class ApprovalActions
{
    public const string StockAdjust = "stock.adjust";
    public const string SalesVoid = "sales.void";
    public static readonly string[] All = [StockAdjust, SalesVoid];
}

public static class ApprovalStatuses
{
    public const string Pending = "Pending";
    /// <summary>Approved and being carried out right now (claimed, so two approvers can never run it twice).</summary>
    public const string Executing = "Executing";
    public const string Executed = "Executed";
    public const string Rejected = "Rejected";
    /// <summary>Approved, but the action itself was refused (e.g. the stock had already gone). Nothing changed.</summary>
    public const string Failed = "Failed";
}

/// <summary>"An action of this kind worth more than ThresholdAmount needs someone else's approval." Off unless the business switches it on.</summary>
public class ApprovalRule
{
    public int Id { get; set; }
    public string Action { get; set; } = "";
    public decimal ThresholdAmount { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>An action waiting for (or decided by) a second person. Payload is exactly what the requester asked for, replayed on approval.</summary>
public class ApprovalRequest
{
    public int Id { get; set; }
    public string Action { get; set; } = "";
    public decimal Amount { get; set; }
    public string Summary { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = ApprovalStatuses.Pending;
    public int RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public int? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public string? FailureReason { get; set; }
}
