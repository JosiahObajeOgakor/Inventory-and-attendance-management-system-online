namespace Inventory.Domain.Rules;

public sealed record BatchSnapshot(int BatchId, int WarehouseId, int QuantityOnHand, DateOnly? ExpiryDate);

public sealed record Allocation(int BatchId, int WarehouseId, int Quantity);

/// <summary>
/// Rule S5: chosen warehouse first, then others; earliest expiry first (no expiry last), then batch id.
/// A line may be split across batches. Pure: the caller loads (and locks) the batches and applies the plan.
/// </summary>
public static class StockAllocator
{
    public static IReadOnlyList<BatchSnapshot> Order(IEnumerable<BatchSnapshot> batches, int preferredWarehouseId) =>
        batches.Where(b => b.QuantityOnHand > 0)
            .OrderBy(b => b.WarehouseId == preferredWarehouseId ? 0 : 1)
            .ThenBy(b => b.ExpiryDate is null ? 1 : 0)
            .ThenBy(b => b.ExpiryDate)
            .ThenBy(b => b.BatchId)
            .ToList();

    /// <returns>The plan, or null when the shelf cannot cover <paramref name="quantity"/>.</returns>
    public static IReadOnlyList<Allocation>? Plan(IEnumerable<BatchSnapshot> batches, int preferredWarehouseId, int quantity)
    {
        var plan = new List<Allocation>();
        var remaining = quantity;
        foreach (var b in Order(batches, preferredWarehouseId))
        {
            if (remaining <= 0) break;
            var take = Math.Min(b.QuantityOnHand, remaining);
            plan.Add(new Allocation(b.BatchId, b.WarehouseId, take));
            remaining -= take;
        }
        return remaining > 0 ? null : plan;
    }
}
