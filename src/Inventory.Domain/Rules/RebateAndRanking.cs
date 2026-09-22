namespace Inventory.Domain.Rules;

/// <summary>
/// Rule S9. The desktop code accrues a rebate on every non-walk-in sale, on net (ex-VAT) sales.
/// Schema comments say "credit customers" — DISCOVERY D11 is open; this reproduces the code.
/// </summary>
public static class RebateCalculator
{
    public static decimal Accrue(string customerType, decimal total, decimal vat, decimal ratePct)
    {
        if (string.Equals(customerType, CustomerTypes.WalkIn, StringComparison.OrdinalIgnoreCase)) return 0m;
        return Money.Round((total - vat) * ratePct / 100m);
    }
}

/// <summary>Rule C3: trailing-12-month spend → Gold/Silver/Bronze (for year-end incentives).</summary>
public static class CustomerRanking
{
    public static string For(decimal trailingTwelveMonthSpend) =>
        trailingTwelveMonthSpend >= 800_000m ? "Gold" : trailingTwelveMonthSpend >= 400_000m ? "Silver" : "Bronze";
}
