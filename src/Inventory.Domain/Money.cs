namespace Inventory.Domain;

/// <summary>
/// Single place for money rounding. The desktop app used VB <c>Math.Round(decimal, 2)</c>, which is
/// banker's rounding (MidpointRounding.ToEven); historical totals depend on it (rule S1).
/// The price-book percentage adjustment is the one exception and uses AwayFromZero.
/// </summary>
public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.ToEven);

    public static decimal RoundAwayFromZero(decimal value, int decimals = 2) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}
