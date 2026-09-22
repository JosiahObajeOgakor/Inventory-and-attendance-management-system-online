using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Purchasing;

public sealed class PurchaseLineDto
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class PurchaseRequest
{
    public int SupplierId { get; set; }
    public DateOnly? OrderDate { get; set; }
    public decimal VatRate { get; set; }
    /// <summary>Goods are already here: book them into stock in the same transaction (P4).</summary>
    public bool ReceiveNow { get; set; }
    /// <summary>Where received goods go; 0 = the first warehouse.</summary>
    public int WarehouseId { get; set; }
    public decimal PaidNow { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public List<PurchaseLineDto> Lines { get; set; } = [];
}

public sealed record PurchaseResult(int PurchaseOrderId, string PoNumber, decimal Subtotal, decimal VatAmount, decimal Total,
    decimal Outstanding, string Status, decimal PreviousBalance, decimal AppliedToPreviousBalance, decimal RemainingBalance);

public sealed class PurchaseRequestValidator : AbstractValidator<PurchaseRequest>
{
    public PurchaseRequestValidator()
    {
        RuleFor(x => x.SupplierId).GreaterThan(0);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.PaidNow).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one line item.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).GreaterThan(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitCost).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>Purchasing (rules P1–P6). Mirror of sales, with the debt running the other way.</summary>
public sealed class PurchaseService(
    IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock,
    ICompanyContext company, IValidator<PurchaseRequest> validator)
{
    private const string Scope = "purchase";

    public async Task<PurchaseResult> SaveAsync(PurchaseRequest req, CurrentUser user, string? idempotencyKey, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        return await tx.RunAsync(inner => SaveOnceAsync(req, user, idempotencyKey, inner), ct);
    }

    private async Task<PurchaseResult> SaveOnceAsync(PurchaseRequest req, CurrentUser user, string? key, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(key))
        {
            var seen = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Scope == Scope && r.Key == key, ct);
            if (seen?.ResultId is int id)
            {
                var po = await db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.Id == id, ct);
                return new PurchaseResult(po.Id, po.PoNumber, 0, 0, po.TotalAmount, po.TotalAmount - po.AmountPaid, po.PaymentStatus, 0, 0, 0);
            }
        }

        var orderDate = req.OrderDate ?? clock.BusinessToday;
        var productIds = req.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != productIds.Count || products.Values.Any(p => !p.IsActive))
            throw new BusinessRuleException("One of the products on this order no longer exists or is inactive.");

        var supplier = await db.Suppliers
            .FromSqlInterpolated($"SELECT * FROM suppliers WHERE Id = {req.SupplierId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Supplier");

        var previous = supplier.Balance;
        var totals = DocumentCalculator.Purchase(req.Lines.Select(l => (l.Quantity, l.UnitCost)), req.VatRate);
        var split = PaymentWaterfall.ForNewDocument(previous, totals.Total, req.PaidNow);

        var number = await DocumentNumbers.NextAsync(company, clock, (n, c) => db.PurchaseOrders.AnyAsync(p => p.PoNumber == n, c), ct);
        var order = new PurchaseOrder
        {
            PoNumber = number, SupplierId = supplier.Id, OrderDate = orderDate, Status = PurchaseStatuses.Pending,
            PaymentStatus = split.Status, TotalAmount = totals.Total, AmountPaid = split.AppliedToNew, CreatedByUserId = user.Id,
            Items = req.Lines.Select(l => new PurchaseOrderItem { ProductId = l.ProductId, Quantity = l.Quantity, UnitCost = l.UnitCost }).ToList(),
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync(ct);

        // Overflow pays older orders, oldest first (P2).
        var appliedToOld = 0m;
        if (split.Overflow > 0)
        {
            var open = await db.PurchaseOrders
                .FromSqlInterpolated($"SELECT * FROM purchase_orders WHERE SupplierId = {supplier.Id} AND PaymentStatus <> 'Paid' AND Id <> {order.Id} ORDER BY OrderDate, Id FOR UPDATE")
                .ToListAsync(ct);
            var (apps, applied) = PaymentWaterfall.Spread(open.Select(o => new OpenDocument(o.Id, o.TotalAmount, o.AmountPaid)), split.Overflow);
            foreach (var a in apps)
            {
                var old = open.First(o => o.Id == a.DocumentId);
                old.AmountPaid += a.Amount;
                old.PaymentStatus = old.AmountPaid >= old.TotalAmount ? PaymentStatuses.Paid : PaymentStatuses.Partial;
            }
            appliedToOld = applied;
        }

        supplier.Balance = supplier.Balance - split.PaidNow + totals.Total;

        // Supplier ledger polarity is the reverse of a customer's (P3): Credit = we owe more, Debit = we paid.
        if (split.Outstanding > 0) db.Ledger.Add(SupplierLedger(orderDate, supplier, LedgerEntryTypes.Credit, split.Outstanding, number));
        if (appliedToOld > 0) db.Ledger.Add(SupplierLedger(orderDate, supplier, LedgerEntryTypes.Debit, appliedToOld, number));

        if (req.ReceiveNow) await ReceiveIntoAsync(order, req.WarehouseId, user, ct);

        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PURCHASE_CREATED", Entity = "PurchaseOrder", EntityId = order.Id.ToString(), At = clock.UtcNow, Detail = number });
        if (!string.IsNullOrEmpty(key)) db.IdempotencyRecords.Add(new IdempotencyRecord { Scope = Scope, Key = key, ResultId = order.Id, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);

        return new PurchaseResult(order.Id, number, totals.Subtotal, totals.VatAmount, totals.Total, split.Outstanding,
            split.Status, previous, appliedToOld, previous - appliedToOld + split.Outstanding);
    }

    /// <summary>
    /// Receive a Pending order later. Replaces the desktop "Receive" button, which had no stock history,
    /// no user, no transaction and could double-receive (defect D2): here it is locked, transactional,
    /// idempotent (a second call is refused) and logs an IN per line.
    /// </summary>
    public Task ReceiveAsync(int purchaseOrderId, int warehouseId, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            var order = await db.PurchaseOrders
                .FromSqlInterpolated($"SELECT * FROM purchase_orders WHERE Id = {purchaseOrderId} FOR UPDATE")
                .Include(p => p.Items)
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Purchase order");
            if (order.Status == PurchaseStatuses.Received) throw new BusinessRuleException("This order has already been received.");
            if (order.Status == PurchaseStatuses.Cancelled) throw new BusinessRuleException("A cancelled order cannot be received.");
            await ReceiveIntoAsync(order, warehouseId, user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PURCHASE_RECEIVED", Entity = "PurchaseOrder", EntityId = order.Id.ToString(), At = clock.UtcNow, Detail = order.PoNumber });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    private async Task ReceiveIntoAsync(PurchaseOrder order, int warehouseId, CurrentUser user, CancellationToken ct)
    {
        if (warehouseId <= 0) warehouseId = await db.Warehouses.MinAsync(w => w.Id, ct);
        else if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, ct)) throw new NotFoundException("Warehouse");

        foreach (var line in order.Items.OrderBy(i => i.ProductId))
        {
            // One batch per purchase, named by its number, so any unit traces back to it (P4).
            await stock.ReceiveAsync(line.ProductId, warehouseId, order.PoNumber, line.Quantity, null,
                MovementReferences.PurchaseOrder, order.Id, user.Id, ct);
            var p = await db.Products.SingleAsync(x => x.Id == line.ProductId, ct);
            if (line.UnitCost > 0) p.CostPrice = line.UnitCost;
            if (string.IsNullOrEmpty(p.Barcode)) p.Barcode = Barcodes.MintInternalBarcode(p.Id);
        }
        order.Status = PurchaseStatuses.Received;
    }

    /// <summary>"Mark paid" (P5): pays whatever remains on this one order.</summary>
    public Task PayAsync(int purchaseOrderId, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            var order = await db.PurchaseOrders
                .FromSqlInterpolated($"SELECT * FROM purchase_orders WHERE Id = {purchaseOrderId} FOR UPDATE")
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Purchase order");
            var supplier = await db.Suppliers
                .FromSqlInterpolated($"SELECT * FROM suppliers WHERE Id = {order.SupplierId} FOR UPDATE")
                .SingleAsync(inner);
            var outstanding = order.TotalAmount - order.AmountPaid;
            if (outstanding <= 0) return true;

            order.AmountPaid = order.TotalAmount;
            order.PaymentStatus = PaymentStatuses.Paid;
            supplier.Balance -= outstanding;
            db.Ledger.Add(SupplierLedger(clock.BusinessToday, supplier, LedgerEntryTypes.Debit, outstanding, order.PoNumber));
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PURCHASE_PAID", Entity = "PurchaseOrder", EntityId = order.Id.ToString(), At = clock.UtcNow, Detail = $"{order.PoNumber} ₦{outstanding:N2}" });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    private static LedgerEntry SupplierLedger(DateOnly date, Supplier s, string type, decimal amount, string reference) => new()
    {
        EntryDate = date, AccountType = LedgerAccountTypes.Supplier, AccountName = s.Name, SupplierId = s.Id,
        EntryType = type, Amount = amount, Reference = reference,
    };
}
