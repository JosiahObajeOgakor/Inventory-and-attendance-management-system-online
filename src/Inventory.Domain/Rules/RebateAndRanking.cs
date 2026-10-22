namespace Inventory.Domain.Rules;

/// <summary>
/// Rule S9, as changed by the business: a rebate is a fixed naira amount per unit (bag) bought, set on each customer, and accrues on
/// every non-walk-in sale. (The desktop app used a percentage of net sales; that rate is kept on old records but no longer used.)
/// </summary>
public static class RebateCalculator
{
    public static decimal Accrue(string customerType, int units, decimal perUnit)
    {
        if (string.Equals(customerType, CustomerTypes.WalkIn, StringComparison.OrdinalIgnoreCase) || units <= 0 || perUnit <= 0) return 0m;
        return Money.Round(units * perUnit);
    }
}

/// <summary>Rule C3: trailing-12-month spend → Gold/Silver/Bronze (for year-end incentives).</summary>
public static class CustomerRanking
{
    public static string For(decimal trailingTwelveMonthSpend) =>
        trailingTwelveMonthSpend >= 800_000m ? "Gold" : trailingTwelveMonthSpend >= 400_000m ? "Silver" : "Bronze";
}
