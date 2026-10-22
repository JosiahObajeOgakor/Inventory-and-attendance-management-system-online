namespace Inventory.Domain.Entities;

public static class BudgetStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
}

/// <summary>A year's budget for the business or one branch: an amount per income or expense account per month.</summary>
public class Budget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Year { get; set; }
    /// <summary>Null = the whole business.</summary>
    public int? BranchId { get; set; }
    public string Status { get; set; } = BudgetStatuses.Draft;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public List<BudgetLine> Lines { get; set; } = [];
}

public class BudgetLine
{
    public long Id { get; set; }
    public int BudgetId { get; set; }
    public string AccountCode { get; set; } = "";
    public int Month { get; set; }
    public decimal Amount { get; set; }
}
