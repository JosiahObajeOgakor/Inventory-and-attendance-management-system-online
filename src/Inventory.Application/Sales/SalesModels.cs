using FluentValidation;
using Inventory.Domain;

namespace Inventory.Application.Sales;

public sealed class SaleLineDto
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    /// <summary>Price charged. When it differs from the product's tier price it is logged as an override (rule S10).</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>For products that track serial numbers: the exact units handed over (count must equal the quantity). Improvement over the desktop app, which never linked serials to a sale.</summary>
    public List<string>? Serials { get; set; }
}

public sealed class SaleRequest
{
    public int CustomerId { get; set; }
    /// <summary>Defaults to today (Lagos). Back-dating is allowed, as in the desktop app.</summary>
    public DateOnly? SaleDate { get; set; }
    public string PriceTier { get; set; } = PriceTiers.Retailer;
    public int WarehouseId { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public decimal DiscountPct { get; set; }
    /// <summary>0 when VAT isn't being charged.</summary>
    public decimal VatRate { get; set; }
    public decimal PaidNow { get; set; }
    public DateOnly? DueDate { get; set; }
    public List<SaleLineDto> Lines { get; set; } = [];
}

public sealed record SaleResult(
    int InvoiceId, string InvoiceNumber, decimal Subtotal, decimal DiscountAmount, decimal VatAmount, decimal Total,
    decimal Outstanding, string Status, decimal PreviousBalance, decimal AppliedToPreviousBalance, decimal RemainingBalance);

public sealed class SaleRequestValidator : AbstractValidator<SaleRequest>
{
    public static readonly string[] PaymentMethods = ["Cash", "Bank Transfer", "Card", "Credit"];

    public SaleRequestValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThan(0);
        RuleFor(x => x.WarehouseId).GreaterThan(0);
        RuleFor(x => x.PriceTier).Must(PriceTiers.IsValid).WithMessage("Unknown price tier.");
        RuleFor(x => x.PaymentMethod).Must(m => PaymentMethods.Contains(m)).WithMessage("Unknown payment method.");
        RuleFor(x => x.DiscountPct).InclusiveBetween(0, 100);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.PaidNow).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one product line.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).GreaterThan(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.Serials).Must(s => s is null || s.Count == 0 || s.All(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= 80)).WithMessage("Serial numbers must be 80 characters or fewer.");
        });
    }
}
