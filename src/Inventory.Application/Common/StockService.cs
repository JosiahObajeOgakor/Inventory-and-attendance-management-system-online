using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Common;

/// <summary>
/// The ONLY writer of stock_batches. Every change writes a stock_movements row in the same
/// transaction (closes DISCOVERY defects D2 and D4). Callers must already be inside a transaction.
/// </summary>
public sealed class StockService(IBusinessDbContext db, IClock clock)
{
    /// <summary>Locks every batch of the given products (ascending product id, so concurrent sales cannot deadlock).</summary>
    public async Task<Dictionary<int, List<StockBatch>>> LockBatchesAsync(IEnumerable<int> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().OrderBy(x => x).Cast<object>().ToArray();
        if (ids.Length == 0) return [];
        var placeholders = string.Join(",", Enumerable.Range(0, ids.Length).Select(i => $"{{{i}}}"));
        var rows = await db.StockBatches
            // placeholders are only "{0},{1},…" indexes; the ids themselves travel as parameters.
            .FromSqlRaw("SELECT * FROM stock_batches WHERE ProductId IN (" + placeholders + ") ORDER BY ProductId, Id FOR UPDATE", ids)
            .ToListAsync(ct);
        var map = rows.GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var id in ids) map.TryAdd((int)id, []);
        return map;
    }

    public Task<List<StockBatch>> LockWarehouseBatchesAsync(int productId, int warehouseId, CancellationToken ct) =>
        db.StockBatches
            .FromSqlInterpolated($"SELECT * FROM stock_batches WHERE ProductId = {productId} AND WarehouseId = {warehouseId} ORDER BY Id FOR UPDATE")
            .ToListAsync(ct);

    /// <summary>Advice used in the "not enough stock" message (rule T4).</summary>
    public async Task<RestockAdvice> AdviseAsync(int productId, int onHand, int reorderLevel, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-RestockAdvisor.DemandWindowDays);
        var sold = await db.StockMovements
            .Where(m => m.ProductId == productId && m.MovementType == MovementTypes.Out
                        && m.ReferenceType != MovementReferences.Transfer && m.MovementDate >= since)
            .SumAsync(m => (int?)m.Quantity, ct) ?? 0;
        return RestockAdvisor.Advise(onHand, reorderLevel, sold);
    }

    /// <summary>Takes <paramref name="quantity"/> off the shelf using the FEFO plan, logging an OUT per batch used.</summary>
    public void Deduct(IReadOnlyList<StockBatch> lockedBatches, int productId, int quantity, int preferredWarehouseId,
        string referenceType, int? referenceId, int userId, string? note = null)
    {
        var plan = StockAllocator.Plan(
            lockedBatches.Select(b => new BatchSnapshot(b.Id, b.WarehouseId, b.QuantityOnHand, b.ExpiryDate)),
            preferredWarehouseId, quantity)
            ?? throw new InvalidOperationException("Stock changed underneath a locked read — refusing to oversell.");

        foreach (var a in plan)
        {
            lockedBatches.First(b => b.Id == a.BatchId).QuantityOnHand -= a.Quantity;
            AddMovement(productId, a.WarehouseId, MovementTypes.Out, a.Quantity, referenceType, referenceId, userId, note, a.BatchId);
        }
    }

    /// <summary>Adds stock to the (product, warehouse, batch number) batch, creating it if needed, and logs an IN.</summary>
    public async Task<StockBatch> ReceiveAsync(int productId, int warehouseId, string batchNumber, int quantity,
        DateOnly? expiry, string referenceType, int? referenceId, int userId, CancellationToken ct, string? note = null)
    {
        if (quantity <= 0) throw new BusinessRuleException("Quantity must be greater than zero.");
        var batch = await AddToBatchAsync(productId, warehouseId, batchNumber, quantity, expiry, ct);
        AddMovement(productId, warehouseId, MovementTypes.In, quantity, referenceType, referenceId, userId, note);
        return batch;
    }

    /// <summary>Moves quantity into a batch WITHOUT logging a movement (the caller logs the business event once).</summary>
    public async Task<StockBatch> AddToBatchAsync(int productId, int warehouseId, string batchNumber, int quantity,
        DateOnly? expiry, CancellationToken ct)
    {
        var batch = await db.StockBatches
            .FromSqlInterpolated($"SELECT * FROM stock_batches WHERE ProductId = {productId} AND WarehouseId = {warehouseId} AND BatchNumber = {batchNumber} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (batch is null)
        {
            batch = new StockBatch { ProductId = productId, WarehouseId = warehouseId, BatchNumber = batchNumber, QuantityOnHand = quantity, ExpiryDate = expiry };
            db.StockBatches.Add(batch);
        }
        else
        {
            batch.QuantityOnHand += quantity;
            if (expiry.HasValue) batch.ExpiryDate = expiry;
        }
        return batch;
    }

    /// <summary>What a sale still holds, per batch: units it took (its sale + any edits) minus units an edit handed back.</summary>
    public sealed record HeldStock(int ProductId, int WarehouseId, int? BatchId, int Quantity, int LastMovementId);

    public async Task<IReadOnlyList<HeldStock>> HeldByInvoiceAsync(int invoiceId, CancellationToken ct)
    {
        var moves = await db.StockMovements.AsNoTracking()
            .Where(m => m.ReferenceId == invoiceId
                        && (m.ReferenceType == MovementReferences.Invoice || m.ReferenceType == MovementReferences.InvoiceEdit)
                        && (m.MovementType == MovementTypes.Out || m.MovementType == MovementTypes.In))
            .ToListAsync(ct);
        return moves.GroupBy(m => (m.ProductId, m.WarehouseId, m.BatchId))
            .Select(g => new HeldStock(g.Key.ProductId, g.Key.WarehouseId, g.Key.BatchId,
                g.Sum(m => m.MovementType == MovementTypes.Out ? m.Quantity : -m.Quantity), g.Max(m => m.Id)))
            .Where(h => h.Quantity > 0)
            .ToList();
    }

    /// <summary>
    /// Puts <paramref name="quantity"/> units of one product that a sale took back on the shelf — into the very batches they came from (most recently
    /// used first), so expiry dates survive. Units recorded without a batch (desktop-era sales) go to the warehouse's shared "RETURNED" batch.
    /// </summary>
    public async Task ReturnFromInvoiceAsync(int invoiceId, int productId, int quantity, string referenceType, int userId, string note, CancellationToken ct)
    {
        var held = (await HeldByInvoiceAsync(invoiceId, ct)).Where(h => h.ProductId == productId).OrderByDescending(h => h.LastMovementId).ToList();
        if (held.Sum(h => h.Quantity) < quantity) throw new InvalidOperationException("Trying to return more units than this sale took.");
        var left = quantity;
        foreach (var h in held)
        {
            if (left == 0) break;
            var n = Math.Min(left, h.Quantity);
            await ReturnHeldAsync(h with { Quantity = n }, invoiceId, referenceType, userId, note, ct);
            left -= n;
        }
    }

    /// <summary>Returns one held slice to its batch (or the RETURNED batch) and logs the IN.</summary>
    public async Task ReturnHeldAsync(HeldStock h, int invoiceId, string referenceType, int userId, string note, CancellationToken ct)
    {
        var batch = h.BatchId is int bid
            ? await db.StockBatches.FromSqlInterpolated($"SELECT * FROM stock_batches WHERE Id = {bid} FOR UPDATE").SingleOrDefaultAsync(ct)
            : null;
        if (batch is not null)
        {
            batch.QuantityOnHand += h.Quantity;
            AddMovement(h.ProductId, h.WarehouseId, MovementTypes.In, h.Quantity, referenceType, invoiceId, userId, note, batch.Id);
        }
        else
        {
            await ReceiveAsync(h.ProductId, h.WarehouseId, "RETURNED", h.Quantity, null, referenceType, invoiceId, userId, ct, note);
        }
    }

    public StockMovement AddMovement(int productId, int warehouseId, string type, int quantity, string? referenceType,
        int? referenceId, int userId, string? note = null, int? batchId = null)
    {
        var m = new StockMovement
        {
            ProductId = productId, WarehouseId = warehouseId, MovementType = type, Quantity = quantity,
            ReferenceType = referenceType, ReferenceId = referenceId, MovementDate = clock.UtcNow, UserId = userId, Note = note, BatchId = batchId,
        };
        db.StockMovements.Add(m);
        return m;
    }
}
