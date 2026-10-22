namespace Inventory.Domain.Entities;

// ================================================================== Time Off

public static class LeaveStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

/// <summary>A kind of leave and how many working days of it an employee gets a year. 0 days = no limit (e.g. unpaid leave).</summary>
public class LeaveType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DaysPerYear { get; set; }
    /// <summary>Paid leave is part of the salary; unpaid leave is reported to payroll as days to deduct.</summary>
    public bool Paid { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public class LeaveRequest
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int LeaveTypeId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    /// <summary>Working days (Monday to Friday) between the two dates, both included.</summary>
    public int Days { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = LeaveStatuses.Pending;
    public int RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }
    public int? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime CreatedAt { get; set; }
}
