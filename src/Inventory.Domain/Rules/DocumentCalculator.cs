namespace Inventory.Domain.Rules;

public sealed record SaleLineInput(int ProductId, int Quantity, decimal UnitPrice);

public sealed record DocumentTotals(decimal Subtotal, decimal DiscountAmount, decimal VatAmount, decimal Total);

/// <summary>Rule S1 (sales) and P1 (purchases).</summary>
public static class DocumentCalculator
{
    public static DocumentTotals Sale(IEnumerable<SaleLineInput> lines, decimal discountPct, decimal vatRate)
    {
        var subtotal = lines.Sum(l => l.Quantity * l.UnitPrice);
        var discount = Money.Round(subtotal * discountPct / 100m);
        var vat = Money.Round((subtotal - discount) * vatRate / 100m);
        return new DocumentTotals(subtotal, discount, vat, subtotal - discount + vat);
    }

    public static DocumentTotals Purchase(IEnumerable<(int Quantity, decimal UnitCost)> lines, decimal vatRate)
    {
        var subtotal = lines.Sum(l => l.Quantity * l.UnitCost);
        var vat = Money.Round(subtotal * vatRate / 100m);
        return new DocumentTotals(subtotal, 0m, vat, subtotal + vat);
    }
}
