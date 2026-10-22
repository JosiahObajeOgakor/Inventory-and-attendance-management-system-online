using Inventory.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// Which warehouse (and so which branch) an online order is booked to. The sale itself can take stock from any warehouse, first-expiring first, with the chosen
/// one preferred; so the books and the branch follow where the goods really come from: the business's default warehouse when it can fill the whole order,
/// else the first warehouse that can, else (an order spread over several) the default, or the one holding the most of the order.
/// </summary>
public static class WarehouseChooser
{
    public static async Task<int> ForOrderAsync(IBusinessDbContext db, ICompanyContext company, IClock clock, IReadOnlyCollection<(int ProductId, int Quantity)> lines, CancellationToken ct)
    {
        var warehouses = await db.Warehouses.AsNoTracking().Select(w => w.Id).ToListAsync(ct);
        if (warehouses.Count == 0) return 0;
        var preferred = warehouses.Contains(company.DefaultWarehouseId) ? company.DefaultWarehouseId : warehouses.Min();
        var order = warehouses.OrderBy(w => w == preferred ? 0 : 1).ThenBy(w => w).ToList();
        var wanted = lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        if (wanted.Count == 0) return preferred;
        var today = clock.BusinessToday;
        var ids = wanted.Keys.ToList();
        var have = await db.StockBatches.AsNoTracking()
            .Where(b => ids.Contains(b.ProductId) && b.QuantityOnHand > 0 && (b.ExpiryDate == null || b.ExpiryDate >= today))
            .GroupBy(b => new { b.WarehouseId, b.ProductId }).Select(g => new { g.Key.WarehouseId, g.Key.ProductId, Qty = g.Sum(b => b.QuantityOnHand) }).ToListAsync(ct);
        int Held(int warehouse, int product) => have.Where(h => h.WarehouseId == warehouse && h.ProductId == product).Sum(h => h.Qty);
        foreach (var w in order)
            if (wanted.All(x => Held(w, x.Key) >= x.Value)) return w;
        // No single warehouse has the whole order: the default if it holds any of it, else the one holding the most.
        return order.OrderByDescending(w => wanted.Sum(x => Math.Min(Held(w, x.Key), x.Value))).ThenBy(w => w == preferred ? 0 : 1).First();
    }
}
