namespace Inventory.Domain.Entities;

public static class DepreciationMethods
{
    public const string StraightLine = "StraightLine";
    public const string ReducingBalance = "ReducingBalance";
    public const string None = "None";   // land
}

public static class AssetStatuses
{
    public const string Active = "Active";
    public const string FullyDepreciated = "FullyDepreciated";
    public const string Disposed = "Disposed";
}

/// <summary>A fixed asset in the register: what it cost, how it wears out, and what it is worth now.</summary>
public class FixedAsset
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? SerialNumber { get; set; }
    public string? Location { get; set; }
    public int? BranchId { get; set; }
    public int? SupplierId { get; set; }
    public DateOnly AcquiredOn { get; set; }
    public decimal Cost { get; set; }
    public decimal ResidualValue { get; set; }
    public string Method { get; set; } = DepreciationMethods.StraightLine;
    /// <summary>Straight line: months over which it is written down.</summary>
    public int UsefulLifeMonths { get; set; }
    /// <summary>Reducing balance: yearly rate on the book value.</summary>
    public decimal RatePct { get; set; }
    /// <summary>Depreciation charged so far, including any brought in with an asset owned before the system started.</summary>
    public decimal AccumulatedDepreciation { get; set; }
    public string Status { get; set; } = AssetStatuses.Active;
    public DateOnly? DisposedOn { get; set; }
    public decimal? DisposalProceeds { get; set; }
    public decimal? DisposalGainLoss { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public decimal BookValue => Cost - AccumulatedDepreciation;
}

/// <summary>One month's depreciation for one asset (an asset is depreciated at most once a month).</summary>
public class AssetDepreciation
{
    public long Id { get; set; }
    public int AssetId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Amount { get; set; }
    public int RunId { get; set; }
}

/// <summary>A month's depreciation run: one journal for every asset depreciated that month.</summary>
public class DepreciationRun
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Total { get; set; }
    public int Assets { get; set; }
    public int RunByUserId { get; set; }
    public DateTime RunAt { get; set; }
}
