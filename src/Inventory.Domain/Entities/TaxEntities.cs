namespace Inventory.Domain.Entities;

public static class TaxKinds
{
    public const string Vat = "VAT";
}

/// <summary>A month's VAT return filed and paid.</summary>
public class TaxFiling
{
    public int Id { get; set; }
    public string Kind { get; set; } = TaxKinds.Vat;
    public int Year { get; set; }
    public int Month { get; set; }
    public string Authority { get; set; } = "NRS";
    /// <summary>Output tax for the month.</summary>
    public decimal TaxDue { get; set; }
    /// <summary>Input tax set off.</summary>
    public decimal Credits { get; set; }
    /// <summary>What was paid to the authority.</summary>
    public decimal AmountPaid { get; set; }
    public DateOnly PaidOn { get; set; }
    public string? Reference { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}
