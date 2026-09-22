namespace Inventory.Domain.Rules;

public sealed record RestockAdvice(
    int OnHand, int ReorderLevel, decimal AvgDailyDemand, decimal? DaysOfCover,
    int ReorderPoint, int SuggestedOrderQty, string Urgency)
{
    public string Summary => AvgDailyDemand <= 0m
        ? $"{OnHand} in stock. No recent sales history, so restock to at least the reorder level ({ReorderLevel})."
        : $"{OnHand} in stock — {(DaysOfCover.HasValue ? $"about {DaysOfCover.Value:0.#} day(s) of cover" : "no cover")} at {AvgDailyDemand:0.#}/day. Suggested restock: {SuggestedOrderQty}.";
}

/// <summary>Rule T4, ported from Stock.AdviseRestock.</summary>
public static class RestockAdvisor
{
    public const int DemandWindowDays = 90;
    public const int LeadTimeDays = 7;
    public const int CoverDays = 30;

    public static RestockAdvice Advise(int onHand, int reorderLevel, int soldInWindow)
    {
        var perDay = Math.Round(soldInWindow / (decimal)DemandWindowDays, 3, MidpointRounding.ToEven);
        var reorderPoint = (int)Math.Ceiling(perDay * (LeadTimeDays + LeadTimeDays / 2m));
        reorderPoint = Math.Max(reorderPoint, reorderLevel);

        var target = (int)Math.Ceiling(perDay * (CoverDays + LeadTimeDays));
        var suggested = Math.Max(0, Math.Max(target, reorderPoint) - onHand);
        if (suggested == 0 && onHand <= reorderPoint) suggested = Math.Max(1, reorderLevel - onHand);

        decimal? cover = perDay > 0m ? Math.Round(onHand / perDay, 1, MidpointRounding.ToEven) : null;
        var urgency = "OK";
        if (onHand <= 0) urgency = "Out of stock";
        else if (onHand <= reorderPoint) urgency = "Reorder now";
        else if (cover.HasValue && cover.Value <= CoverDays) urgency = "Low stock";

        return new RestockAdvice(onHand, reorderLevel, perDay, cover, reorderPoint, suggested, urgency);
    }
}
