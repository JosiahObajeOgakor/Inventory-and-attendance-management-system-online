namespace Inventory.Domain.Rules;

public sealed record SaleLineInput(int ProductId, int Quantity, decimal UnitPrice);

public sealed record DocumentTotals(decimal Subtotal, decimal DiscountAmount, decimal VatAmount, decimal Total);

/// <summary>Rule S1 (sales) and P1 (purchases).</summary>
public static class DocumentCalculator
{
    /// <param name="discountAmount">A fixed naira discount ("₦2,000 off"). When given (&gt; 0) it is used exactly, capped at the subtotal,
    /// and <paramref name="discountPct"/> is ignored — converting it to a 2-decimal percentage would not give back the same naira.</param>
    public static DocumentTotals Sale(IEnumerable<SaleLineInput> lines, decimal discountPct, decimal vatRate, decimal? discountAmount = null)
    {
        var subtotal = lines.Sum(l => l.Quantity * l.UnitPrice);
        var discount = discountAmount is > 0 ? Money.Round(Math.Min(discountAmount.Value, subtotal)) : Money.Round(subtotal * discountPct / 100m);
        var vat = Money.Round((subtotal - discount) * vatRate / 100m);
        return new DocumentTotals(subtotal, discount, vat, subtotal - discount + vat);
    }

    /// <summary>The percentage to record and print for a discount (2 decimals, display only — the amount is what's charged).</summary>
    public static decimal EffectivePct(decimal subtotal, decimal discount) =>
        subtotal <= 0 ? 0m : Math.Min(100m, Math.Round(discount / subtotal * 100m, 2, MidpointRounding.AwayFromZero));

    public static DocumentTotals Purchase(IEnumerable<(int Quantity, decimal UnitCost)> lines, decimal vatRate)
    {
        var subtotal = lines.Sum(l => l.Quantity * l.UnitCost);
        var vat = Money.Round(subtotal * vatRate / 100m);
        return new DocumentTotals(subtotal, 0m, vat, subtotal + vat);
    }
}
