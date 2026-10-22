using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Purchasing;

public sealed class PurchaseEditRequest
{
    public decimal VatRate { get; set; }
    public List<PurchaseLineDto> Lines { get; set; } = [];
}

public sealed record PurchaseStockChange(int ProductId, string Product, int Quantity);

public sealed record PurchaseEditResult(int PurchaseOrderId, string PoNumber, decimal OldTotal, decimal NewTotal, decimal Outstanding, string PaymentStatus,
    IReadOnlyList<PurchaseStockChange> StockIn, IReadOnlyList<PurchaseStockChange> StockOut, decimal MovedToOtherOrders, decimal CreditHeld);

public sealed class PurchaseEditRequestValidator : AbstractValidator<PurchaseEditRequest>
{
    public PurchaseEditRequestValidator()
    {
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("A purchase needs at least one line item.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).GreaterThan(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitCost).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>
/// Changes the lines of a purchase order. Before it's received only the order and what we owe change. After it's received, stock moves by the
/// DIFFERENCE, in this order's own batch (named after the PO number): extra units go in, and units taken off only come out of that batch while
/// they are still on the shelf — units already sold can't be un-bought. What we owe the supplier follows, with polarity the reverse of a customer's.
/// </summary>
public sealed class PurchaseEditService(IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock)
{
    public async Task<PurchaseEditResult> EditAsync(int purchaseOrderId, PurchaseEditRequest req, CurrentUser user, CancellationToken ct = default)
    {
        await new PurchaseEditRequestValidator().ValidateAndThrowAsync(req, ct);
        return await tx.RunAsync(inner => EditOnceAsync(purchaseOrderId, req, user, inner), ct);
    }

    private async Task<PurchaseEditResult> EditOnceAsync(int id, PurchaseEditRequest req, CurrentUser user, CancellationToken ct)
    {
        var peek = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order");
        var supplier = await db.Suppliers.FromSqlInterpolated($"SELECT * FROM suppliers WHERE Id = {peek.SupplierId} FOR UPDATE").SingleAsync(ct);
        var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM purchase_orders WHERE Id = {id} FOR UPDATE").Include(p => p.Items).SingleAsync(ct);
        if (order.Status == PurchaseStatuses.Cancelled) throw new BusinessRuleException("A cancelled order can't be edited.");

        var productIds = order.Items.Select(i => i.ProductId).Concat(req.Lines.Select(l => l.ProductId)).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var before = order.Items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var after = req.Lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        foreach (var pid in after.Keys.Where(pid => !before.ContainsKey(pid)))
            if (!products.TryGetValue(pid, out var p) || !p.IsActive) throw new BusinessRuleException("One of the added products no longer exists or is inactive.");

        var stockIn = new List<PurchaseStockChange>(); var stockOut = new List<PurchaseStockChange>();
        if (order.Status == PurchaseStatuses.Received)
        {
            // The warehouse the order went into (its receipt movements say so); new lines go to the same place.
            var warehouseId = await db.StockMovements.AsNoTracking()
                .Where(m => m.ReferenceType == MovementReferences.PurchaseOrder && m.ReferenceId == order.Id)
                .Select(m => (int?)m.WarehouseId).FirstOrDefaultAsync(ct) ?? await db.Warehouses.MinAsync(w => w.Id, ct);
            var note = $"Edit of {order.PoNumber}";

            // Check every reduction first, so a refusal changes nothing.
            var batches = new Dictionary<int, StockBatch?>();
            var problems = new List<string>();
            foreach (var pid in productIds.OrderBy(x => x))
            {
                var delta = after.GetValueOrDefault(pid) - before.GetValueOrDefault(pid);
                if (delta >= 0) continue;
                var batch = await db.StockBatches
                    .FromSqlInterpolated($"SELECT * FROM stock_batches WHERE ProductId = {pid} AND WarehouseId = {warehouseId} AND BatchNumber = {order.PoNumber} FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                batches[pid] = batch;
                var onShelf = batch?.QuantityOnHand ?? 0;
                if (-delta > onShelf)
                    problems.Add($"{products[pid].Name}: {before.GetValueOrDefault(pid) - onShelf} of the {before.GetValueOrDefault(pid)} received are already sold or moved, so you can reduce it by at most {onShelf}.");
            }
            if (problems.Count > 0) throw new BusinessRuleException(string.Join(" ", problems));

            foreach (var pid in productIds.OrderBy(x => x))
            {
                var delta = after.GetValueOrDefault(pid) - before.GetValueOrDefault(pid);
                if (delta > 0)
                {
                    await stock.ReceiveAsync(pid, warehouseId, order.PoNumber, delta, null, MovementReferences.PurchaseEdit, order.Id, user.Id, ct, note);
                    stockIn.Add(new PurchaseStockChange(pid, products[pid].Name, delta));
                }
                else if (delta < 0)
                {
                    var batch = batches[pid]!;
                    batch.QuantityOnHand += delta;
                    stock.AddMovement(pid, warehouseId, MovementTypes.Out, -delta, MovementReferences.PurchaseEdit, order.Id, user.Id, note, batch.Id);
                    stockOut.Add(new PurchaseStockChange(pid, products[pid].Name, -delta));
                }
            }
            // The corrected cost becomes the product's cost price, exactly as receiving the order did.
            foreach (var l in req.Lines.Where(l => l.UnitCost > 0)) products[l.ProductId].CostPrice = l.UnitCost;
        }

        db.PurchaseOrderItems.RemoveRange(order.Items);
        foreach (var l in req.Lines)
            db.PurchaseOrderItems.Add(new PurchaseOrderItem { PurchaseOrderId = order.Id, ProductId = l.ProductId, Quantity = l.Quantity, UnitCost = l.UnitCost });

        var totals = DocumentCalculator.Purchase(req.Lines.Select(l => (l.Quantity, l.UnitCost)), req.VatRate);
        var oldTotal = order.TotalAmount;
        var newTotal = totals.Total;
        order.TotalAmount = newTotal;

        // What we owe the supplier follows. Supplier ledger polarity (P3): Credit = we owe more, Debit = we owe less.
        var change = newTotal - oldTotal;
        supplier.Balance += change;
        if (change != 0)
            db.Ledger.Add(new LedgerEntry
            {
                EntryDate = clock.BusinessToday, AccountType = LedgerAccountTypes.Supplier, AccountName = supplier.Name, SupplierId = supplier.Id,
                EntryType = change > 0 ? LedgerEntryTypes.Credit : LedgerEntryTypes.Debit, Amount = Math.Abs(change), Reference = "EDIT " + order.PoNumber,
            });

        decimal moved = 0, credit = 0;
        if (order.AmountPaid > newTotal)
        {
            // We paid more than the order now costs: the extra settles our other unpaid orders with this supplier, the rest is credit with them.
            var excess = order.AmountPaid - newTotal;
            order.AmountPaid = newTotal;
            var open = await db.PurchaseOrders
                .FromSqlInterpolated($"SELECT * FROM purchase_orders WHERE SupplierId = {supplier.Id} AND PaymentStatus <> 'Paid' AND Status <> 'Cancelled' AND Id <> {order.Id} ORDER BY OrderDate, Id FOR UPDATE")
                .ToListAsync(ct);
            var (apps, applied) = PaymentWaterfall.Spread(open.Select(o => new OpenDocument(o.Id, o.TotalAmount, o.AmountPaid)), excess);
            foreach (var a in apps)
            {
                var other = open.First(o => o.Id == a.DocumentId);
                other.AmountPaid += a.Amount;
                other.PaymentStatus = PaymentStatuses.For(other.AmountPaid, other.TotalAmount);
            }
            moved = applied;
            credit = excess - applied;
        }
        order.PaymentStatus = PaymentStatuses.For(order.AmountPaid, newTotal);

        var summary = string.Join("; ", new[]
        {
            stockIn.Count > 0 ? "Added to stock: " + string.Join(", ", stockIn.Select(s => $"{s.Quantity} × {s.Product}")) : null,
            stockOut.Count > 0 ? "Taken out of stock: " + string.Join(", ", stockOut.Select(s => $"{s.Quantity} × {s.Product}")) : null,
            $"total ₦{oldTotal:N2} → ₦{newTotal:N2}",
            moved > 0 ? $"₦{moved:N2} moved to other unpaid orders" : null,
            credit > 0 ? $"₦{credit:N2} held as credit with the supplier" : null,
        }.Where(s => s is not null));
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PURCHASE_EDITED", Entity = "PurchaseOrder", EntityId = order.Id.ToString(), At = clock.UtcNow,
            Detail = (order.PoNumber + ": " + summary) is var d && d.Length > 1000 ? d[..1000] : order.PoNumber + ": " + summary });
        await db.SaveChangesAsync(ct);

        return new PurchaseEditResult(order.Id, order.PoNumber, oldTotal, newTotal, newTotal - order.AmountPaid, order.PaymentStatus, stockIn, stockOut, moved, credit);
    }
}
