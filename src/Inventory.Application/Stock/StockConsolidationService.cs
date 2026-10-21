using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Stock;

public sealed record ConsolidationPreview(int DuplicateProductNames, int ProductsToMerge, int StockRowGroups, int StockRowsToMerge, int EmptyRowsToRemove, IReadOnlyList<string> Examples)
{
    public bool NothingToDo => ProductsToMerge == 0 && StockRowsToMerge == 0;
}

public sealed record ConsolidationResult(int ProductsMerged, int StockRowsMerged, int StockOnHandBefore, int StockOnHandAfter);

/// <summary>
/// Tidies what repeated purchases and repeated entry left behind: products that share a name become ONE product, and a product's stock in a warehouse becomes ONE row per expiry date.
/// Nothing is lost: quantities are added together (checked: the grand total on hand must be identical afterwards, or everything is rolled back), and every sale, purchase, quotation,
/// stock-history and serial-number record is re-pointed to the surviving product / row. It runs in one transaction.
/// </summary>
public sealed class StockConsolidationService(IBusinessDbContext db, TransactionRunner tx, IClock clock)
{
    private sealed record P(int Id, string Name, bool IsActive);
    private sealed record B(int Id, int ProductId, int WarehouseId, DateOnly? Expiry, int Qty);

    private async Task<(List<P> Products, List<B> Batches)> LoadAsync(CancellationToken ct) => (
        await db.Products.AsNoTracking().Select(p => new P(p.Id, p.Name, p.IsActive)).ToListAsync(ct),
        await db.StockBatches.AsNoTracking().Select(b => new B(b.Id, b.ProductId, b.WarehouseId, b.ExpiryDate, b.QuantityOnHand)).ToListAsync(ct));

    /// <summary>Product id → the id it will be merged into (itself when it is not a duplicate). The survivor is the oldest product with that name.</summary>
    private static Dictionary<int, int> Survivors(List<P> products)
    {
        var map = products.ToDictionary(p => p.Id, p => p.Id);
        foreach (var g in products.GroupBy(p => ProductNames.Key(p.Name)).Where(g => g.Count() > 1))
        {
            var survivor = g.OrderByDescending(p => p.IsActive).ThenBy(p => p.Id).First().Id;     // prefer a live product; then the oldest
            foreach (var p in g) map[p.Id] = survivor;
        }
        return map;
    }

    public async Task<ConsolidationPreview> PreviewAsync(CancellationToken ct)
    {
        var (products, batches) = await LoadAsync(ct);
        var survivors = Survivors(products);
        var names = products.ToDictionary(p => p.Id, p => p.Name);
        var dupGroups = products.GroupBy(p => ProductNames.Key(p.Name)).Where(g => g.Count() > 1).ToList();
        var groups = batches.GroupBy(b => (Product: survivors[b.ProductId], b.WarehouseId, b.Expiry)).Where(g => g.Count() > 1).ToList();
        var examples = dupGroups.Take(3).Select(g => $"{g.Count()} products named \"{names[g.First().Id]}\" become one")
            .Concat(groups.Take(5).Select(g => $"{names[g.Key.Product]}: {g.Count()} stock rows ({g.Sum(b => b.Qty):N0} in total) become one")).ToList();
        var mergeCount = groups.Sum(g => g.Count() - 1);
        var emptyAfter = groups.Sum(g => g.Count(b => b.Qty == 0));
        return new ConsolidationPreview(dupGroups.Count, dupGroups.Sum(g => g.Count() - 1), groups.Count, mergeCount, emptyAfter, examples);
    }

    public Task<ConsolidationResult> RunAsync(CurrentUser user, CancellationToken ct) =>
        tx.RunAsync(async inner =>
        {
            // Hold every stock row for the duration: a sale mid-merge would otherwise deduct from a row that is about to disappear.
            await db.StockBatches.FromSqlRaw("SELECT * FROM stock_batches ORDER BY Id FOR UPDATE").AsNoTracking().ToListAsync(inner);
            var (products, batches) = await LoadAsync(inner);
            var before = batches.Sum(b => b.Qty);
            var orig = await db.StockBatches.AsNoTracking().Select(b => new { b.Id, b.BatchNumber }).ToDictionaryAsync(b => b.Id, b => b.BatchNumber, inner);
            var survivors = Survivors(products);
            var productsMerged = 0;

            // 1. Duplicate products: re-point everything that refers to a duplicate at the survivor, then remove the duplicate.
            foreach (var (dupId, survivorId) in survivors.Where(kv => kv.Key != kv.Value).OrderBy(kv => kv.Key))
            {
                await db.InvoiceItems.Where(x => x.ProductId == dupId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId), inner);
                await db.PurchaseOrderItems.Where(x => x.ProductId == dupId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId), inner);
                await db.QuotationItems.Where(x => x.ProductId == dupId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId), inner);
                await db.StockMovements.Where(x => x.ProductId == dupId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId), inner);
                await db.ProductSerials.Where(x => x.ProductId == dupId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId), inner);
                await db.PriceChanges.Where(x => x.ProductId == dupId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId), inner);
                // A batch name can only exist once per (product, warehouse): rows that would collide are merged in step 2 under a temporary unique name.
                var dupBatches = await db.StockBatches.Where(x => x.ProductId == dupId).Select(x => x.Id).ToListAsync(inner);
                foreach (var id in dupBatches)
                    await db.StockBatches.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProductId, survivorId).SetProperty(x => x.BatchNumber, "~" + id), inner);
                var dup = await db.Products.AsNoTracking().Where(x => x.Id == dupId).Select(x => new { x.Sku, x.Name, x.Barcode }).SingleAsync(inner);
                db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PRODUCT_MERGED", Entity = "Product", EntityId = survivorId.ToString(), At = clock.UtcNow, Detail = $"{dup.Name} (SKU {dup.Sku}, product {dupId}) merged into product {survivorId}" });
                await db.SaveChangesAsync(inner);
                await db.Products.Where(x => x.Id == dupId).ExecuteDeleteAsync(inner);
                productsMerged++;
            }
            // A merged product is live if any of its copies was.
            foreach (var g in products.GroupBy(p => survivors[p.Id]).Where(g => g.Count() > 1 && g.Any(p => p.IsActive)))
                await db.Products.Where(x => x.Id == g.Key).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, true), inner);

            // 2. One row per (product, warehouse, expiry): the oldest row survives with the combined quantity; history is re-pointed at it.
            var current = await db.StockBatches.AsNoTracking().Select(b => new B(b.Id, b.ProductId, b.WarehouseId, b.ExpiryDate, b.QuantityOnHand)).ToListAsync(inner);
            var numbers = await db.StockBatches.AsNoTracking().Select(b => new { b.Id, b.BatchNumber }).ToDictionaryAsync(b => b.Id, b => b.BatchNumber, inner);
            var rowsMerged = 0;
            foreach (var g in current.GroupBy(b => (b.ProductId, b.WarehouseId, b.Expiry)).Where(g => g.Count() > 1))
            {
                var ordered = g.OrderBy(b => b.Id).ToList();
                var keep = ordered.FirstOrDefault(b => !numbers[b.Id].StartsWith('~')) ?? ordered[0];       // keep a real batch name where one exists
                var others = ordered.Where(b => b.Id != keep.Id).ToList();
                var sum = ordered.Sum(b => b.Qty);
                foreach (var o in others)
                {
                    await db.StockMovements.Where(x => x.BatchId == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.BatchId, keep.Id), inner);
                    // Production and manual-adjustment history point at their batch through ReferenceId.
                    await db.StockMovements.Where(x => x.ReferenceId == o.Id && (x.ReferenceType == MovementReferences.Production || x.ReferenceType == MovementReferences.Manual))
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.ReferenceId, keep.Id), inner);
                    await db.ProductSerials.Where(x => x.BatchId == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.BatchId, keep.Id), inner);
                    await db.StockBatches.Where(x => x.Id == o.Id).ExecuteDeleteAsync(inner);
                    rowsMerged++;
                }
                var wanted = numbers[keep.Id].StartsWith('~') ? orig[keep.Id] : numbers[keep.Id];
                var finalNumber = await FreeNameAsync(keep.Id, keep.ProductId, keep.WarehouseId, wanted, keep.Expiry, inner);
                await db.StockBatches.Where(x => x.Id == keep.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.QuantityOnHand, sum).SetProperty(x => x.BatchNumber, finalNumber), inner);
            }
            // Any row still carrying a temporary name (a duplicate product's only row in a warehouse) gets its own name back.
            foreach (var b in await db.StockBatches.AsNoTracking().Where(x => x.BatchNumber.StartsWith("~")).ToListAsync(inner))
            {
                var name = await FreeNameAsync(b.Id, b.ProductId, b.WarehouseId, orig[b.Id], b.ExpiryDate, inner);
                await db.StockBatches.Where(x => x.Id == b.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.BatchNumber, name), inner);
            }

            var after = await db.StockBatches.AsNoTracking().SumAsync(b => (int?)b.QuantityOnHand, inner) ?? 0;
            if (after != before) throw new InvalidOperationException($"Stock on hand changed during the tidy-up ({before} → {after}); nothing was saved.");

            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "STOCK_CONSOLIDATED", Entity = "StockBatch", At = clock.UtcNow, Detail = $"{productsMerged} product(s) merged, {rowsMerged} stock row(s) merged; {before} on hand before and after" });
            await db.SaveChangesAsync(inner);
            return new ConsolidationResult(productsMerged, rowsMerged, before, after);
        }, ct);

    /// <summary>The wanted batch name if no other row of this product in this warehouse uses it; otherwise the standard shelf name (made unique if needed).</summary>
    private async Task<string> FreeNameAsync(int rowId, int productId, int warehouseId, string wanted, DateOnly? expiry, CancellationToken ct)
    {
        async Task<bool> Taken(string n) => await db.StockBatches.AnyAsync(x => x.ProductId == productId && x.WarehouseId == warehouseId && x.BatchNumber == n && x.Id != rowId, ct);
        if (!await Taken(wanted)) return wanted;
        var shelf = StockService.ShelfBatchNumber(expiry);
        return await Taken(shelf) ? $"{shelf}-{rowId}" : shelf;
    }

    /// <summary>
    /// Makes the database itself refuse a second product with the same name. Created only when no duplicates remain (otherwise it would fail), so call it after a tidy-up.
    /// Safe to repeat. Returns whether the guard is now in place.
    /// </summary>
    public async Task<bool> EnsureUniqueNameGuardAsync(CancellationToken ct)
    {
        var exists = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'products' AND index_name = 'IX_products_Name'").SingleAsync(ct);
        if (exists > 0) return true;
        var dups = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM (SELECT Name FROM products GROUP BY Name HAVING COUNT(*) > 1) d").SingleAsync(ct);
        if (dups > 0) return false;
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IX_products_Name ON products (Name)", ct);
        return true;
    }
}
