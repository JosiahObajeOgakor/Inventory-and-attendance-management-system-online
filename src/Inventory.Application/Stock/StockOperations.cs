using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Stock;

/// <summary>Stock movements that aren't part of a sale or purchase (rules T1–T3, T6). Ported from Stock.vb.</summary>
public sealed class StockOperations(IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock)
{
    /// <summary>T1: a production run into a warehouse. Blank batch → "PROD-yyMMdd" of the production date.</summary>
    public Task<int> RecordProductionAsync(int productId, int warehouseId, int quantity, DateOnly producedOn,
        string? batchNumber, DateOnly? expiry, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            if (quantity <= 0) throw new BusinessRuleException("Enter how many were produced.");
            await EnsureProductAndWarehouse(productId, warehouseId, inner);
            var batch = string.IsNullOrWhiteSpace(batchNumber) ? "PROD-" + producedOn.ToString("yyMMdd") : batchNumber.Trim();
            var b = await stock.AddToBatchAsync(productId, warehouseId, batch, quantity, expiry, inner);
            await db.SaveChangesAsync(inner);   // batch id needed as the movement's reference
            var m = stock.AddMovement(productId, warehouseId, MovementTypes.In, quantity, MovementReferences.Production, b.Id, user.Id);
            m.MovementDate = producedOn.ToDateTime(TimeOnly.MinValue) - TimeSpan.FromHours(1);   // production date, Lagos midnight, stored UTC
            db.AuditLogs.Add(Audit(user, "PRODUCTION_RECORDED", "StockBatch", b.Id.ToString(), $"{quantity} of product {productId}"));
            await db.SaveChangesAsync(inner);
            return m.Id;
        }, ct);

    /// <summary>
    /// T2: corrects a production entry. Stock moves by the difference; reductions come from the entry's own
    /// batch, then that day's PROD- batch, then the newest others, and are refused if that much is gone.
    /// Unlike the desktop app, a quantity of 0 keeps the history row (quantity 0, noted) instead of deleting it (defect D6).
    /// </summary>
    public Task CorrectProductionAsync(int movementId, int newQuantity, DateOnly producedOn, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (newQuantity < 0) throw new BusinessRuleException("Quantity cannot be negative.");
            var m = await db.StockMovements
                .FromSqlInterpolated($"SELECT * FROM stock_movements WHERE Id = {movementId} AND ReferenceType = 'Production' FOR UPDATE")
                .SingleOrDefaultAsync(inner) ?? throw new BusinessRuleException("That production entry no longer exists.");

            var oldQty = m.Quantity;
            var delta = newQuantity - oldQty;
            var dayBatch = "PROD-" + DateOnly.FromDateTime(m.MovementDate + TimeSpan.FromHours(1)).ToString("yyMMdd");
            var batches = await stock.LockWarehouseBatchesAsync(m.ProductId, m.WarehouseId, inner);

            if (delta > 0)
            {
                var target = batches.FirstOrDefault(b => b.Id == m.ReferenceId) ?? batches.FirstOrDefault(b => b.BatchNumber == dayBatch);
                if (target is not null) target.QuantityOnHand += delta;
                else await stock.AddToBatchAsync(m.ProductId, m.WarehouseId, dayBatch, delta, null, inner);
            }
            else if (delta < 0)
            {
                var need = -delta;
                var ordered = batches.Where(b => b.QuantityOnHand > 0)
                    .OrderBy(b => b.Id == m.ReferenceId ? 0 : b.BatchNumber == dayBatch ? 1 : 2).ThenByDescending(b => b.Id).ToList();
                var have = ordered.Sum(b => b.QuantityOnHand);
                if (have < need)
                    throw new BusinessRuleException($"Only {have:N0} left in that warehouse — the rest has already been sold or moved. This entry can't go below {oldQty - have:N0}.");
                foreach (var b in ordered)
                {
                    if (need == 0) break;
                    var take = Math.Min(need, b.QuantityOnHand);
                    b.QuantityOnHand -= take;
                    need -= take;
                }
            }

            m.Quantity = newQuantity;
            m.MovementDate = producedOn.ToDateTime(TimeOnly.MinValue) - TimeSpan.FromHours(1);
            if (newQuantity == 0) m.Note = "Entry deleted by " + user.FullName;
            db.AuditLogs.Add(Audit(user, newQuantity == 0 ? "PRODUCTION_DELETED" : "PRODUCTION_CORRECTED", "StockMovement", m.Id.ToString(), $"{oldQty} -> {newQuantity}"));
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    /// <summary>T3: warehouse-to-warehouse transfer; earliest expiry first; batch number and expiry preserved; one OUT + one IN.</summary>
    public Task TransferAsync(int productId, int fromWarehouseId, int toWarehouseId, int quantity, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (quantity <= 0) throw new BusinessRuleException("Enter how many to move.");
            if (fromWarehouseId == toWarehouseId) throw new BusinessRuleException("Pick two different warehouses.");
            await EnsureProductAndWarehouse(productId, fromWarehouseId, inner);
            await EnsureProductAndWarehouse(productId, toWarehouseId, inner);

            var batches = await stock.LockWarehouseBatchesAsync(productId, fromWarehouseId, inner);
            var plan = StockAllocator.Plan(batches.Select(b => new BatchSnapshot(b.Id, b.WarehouseId, b.QuantityOnHand, b.ExpiryDate)), fromWarehouseId, quantity);
            if (plan is null)
            {
                var have = batches.Sum(b => b.QuantityOnHand);
                var name = await db.Warehouses.Where(w => w.Id == fromWarehouseId).Select(w => w.Name).SingleAsync(inner);
                throw new BusinessRuleException($"Only {have:N0} in {name} — can't move {quantity:N0}.");
            }
            foreach (var a in plan)
            {
                var src = batches.First(b => b.Id == a.BatchId);
                src.QuantityOnHand -= a.Quantity;
                await stock.AddToBatchAsync(productId, toWarehouseId, src.BatchNumber, a.Quantity, src.ExpiryDate, inner);
            }
            stock.AddMovement(productId, fromWarehouseId, MovementTypes.Out, quantity, MovementReferences.Transfer, toWarehouseId, user.Id);
            stock.AddMovement(productId, toWarehouseId, MovementTypes.In, quantity, MovementReferences.Transfer, fromWarehouseId, user.Id);
            db.AuditLogs.Add(Audit(user, "STOCK_TRANSFERRED", "Product", productId.ToString(), $"{quantity}: {fromWarehouseId} -> {toWarehouseId}"));
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    /// <summary>
    /// T6 (NEW — the desktop app has no stock count/adjustment): a signed correction to one batch with a mandatory reason.
    /// Logged as an ADJUST movement whose quantity is signed; never lets a batch go negative.
    /// </summary>
    public Task AdjustAsync(int batchId, int delta, string reason, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (delta == 0) throw new BusinessRuleException("Nothing to adjust.");
            if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("A reason is required for a stock adjustment.");
            var batch = await db.StockBatches
                .FromSqlInterpolated($"SELECT * FROM stock_batches WHERE Id = {batchId} FOR UPDATE")
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Batch");
            if (batch.QuantityOnHand + delta < 0)
                throw new BusinessRuleException($"Only {batch.QuantityOnHand:N0} in that batch — it can't be reduced by {-delta:N0}.");
            batch.QuantityOnHand += delta;
            stock.AddMovement(batch.ProductId, batch.WarehouseId, MovementTypes.Adjust, delta, MovementReferences.Manual, batch.Id, user.Id, reason.Trim());
            db.AuditLogs.Add(Audit(user, "STOCK_ADJUSTED", "StockBatch", batch.Id.ToString(), $"{delta:+#;-#}: {reason.Trim()}"));
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    private async Task EnsureProductAndWarehouse(int productId, int warehouseId, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) throw new NotFoundException("Product");
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, ct)) throw new NotFoundException("Warehouse");
    }

    private AuditLog Audit(CurrentUser u, string action, string entity, string id, string detail) => new()
    {
        UserId = u.Id, UserName = u.FullName, Action = action, Entity = entity, EntityId = id, At = clock.UtcNow, Detail = detail,
    };
}
