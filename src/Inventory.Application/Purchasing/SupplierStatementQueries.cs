using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Queries;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Purchasing;

/// <summary>One of the supplier's goods: the usual cost on their list, and what we have actually bought from them.</summary>
public sealed record SupplierStatementItem(int ProductId, string Product, string Sku, string Unit, decimal UsualCost, bool OnList, bool IsActive,
    int QuantityBought, decimal AmountBought, decimal? LastCost, DateOnly? LastBought);
public sealed record SupplierStatementOrder(int Id, string PoNumber, DateOnly OrderDate, string Status, string PaymentStatus, decimal Total, decimal Paid,
    decimal Outstanding, int Lines, int Units);
/// <summary>One payment as the person made it (possibly spread over several orders — <see cref="Orders"/> lists them).</summary>
public sealed record SupplierStatementPayment(string Reference, DateTime PaidAt, string Method, decimal Amount, IReadOnlyList<string> Orders);
public sealed record SupplierStatement(SupplierDto Supplier, decimal TotalBought, decimal TotalPaid, decimal Owed, int Orders, int OpenOrders,
    DateOnly? LastOrder, IReadOnlyList<SupplierStatementItem> Items, IReadOnlyList<SupplierStatementOrder> OrderList, IReadOnlyList<SupplierStatementPayment> Payments);

/// <summary>
/// Everything tied to one supplier on a single screen: what they sell us, every order (paid / part-paid / owed), every payment we made, and the
/// running total we owe. Read-only; the figures come straight from the orders and payment rows, the balance from the supplier's running total.
/// </summary>
public sealed class SupplierStatementQueries(IBusinessDbContext db)
{
    public async Task<SupplierStatement> GetAsync(int supplierId, CancellationToken ct)
    {
        var s = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == supplierId, ct) ?? throw new NotFoundException("Supplier");

        var orders = await db.PurchaseOrders.AsNoTracking().Include(p => p.Items).Where(p => p.SupplierId == supplierId)
            .OrderByDescending(p => p.OrderDate).ThenByDescending(p => p.Id).ToListAsync(ct);
        var live = orders.Where(o => o.Status != PurchaseStatuses.Cancelled).ToList();

        // ---- goods: the supplier's list, plus anything bought from them that isn't on it (so nothing they've supplied is hidden)
        var listed = await db.SupplierItems.AsNoTracking().Where(x => x.SupplierId == supplierId).ToDictionaryAsync(x => x.ProductId, ct);
        var lines = live.SelectMany(o => o.Items.Select(i => new { o.OrderDate, o.Id, i.ProductId, i.Quantity, i.UnitCost })).ToList();
        var productIds = listed.Keys.Concat(lines.Select(l => l.ProductId)).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var items = productIds.Where(products.ContainsKey).Select(id =>
        {
            var p = products[id];
            var bought = lines.Where(l => l.ProductId == id).OrderByDescending(l => l.OrderDate).ThenByDescending(l => l.Id).ToList();
            var usual = listed.TryGetValue(id, out var li) && li.UnitCost > 0 ? li.UnitCost : p.CostPrice;
            return new SupplierStatementItem(id, p.Name, p.Sku, p.Unit, usual, listed.ContainsKey(id), p.IsActive,
                bought.Sum(l => l.Quantity), bought.Sum(l => l.Quantity * l.UnitCost), bought.FirstOrDefault()?.UnitCost, bought.FirstOrDefault()?.OrderDate);
        }).OrderByDescending(i => i.OnList).ThenBy(i => i.Product).ToList();

        var orderRows = orders.Select(o => new SupplierStatementOrder(o.Id, o.PoNumber, o.OrderDate, o.Status, o.PaymentStatus, o.TotalAmount, o.AmountPaid,
            o.Status == PurchaseStatuses.Cancelled ? 0 : Math.Max(0, o.TotalAmount - o.AmountPaid), o.Items.Count, o.Items.Sum(i => i.Quantity))).ToList();

        // ---- payments, one line per payment the person made
        var numbers = orders.ToDictionary(o => o.Id, o => o.PoNumber);
        var rows = await db.SupplierPayments.AsNoTracking().Where(x => x.SupplierId == supplierId).ToListAsync(ct);
        var payments = rows.GroupBy(r => new { r.Reference, r.Method, Minute = r.PaidAt.ToString("yyyyMMddHHmm") })
            .Select(g => new SupplierStatementPayment(g.Key.Reference, g.Min(r => r.PaidAt), g.Key.Method, g.Sum(r => r.Amount),
                g.Select(r => numbers.GetValueOrDefault(r.PurchaseOrderId, "")).Where(n => n.Length > 0).Distinct().ToList()))
            .OrderByDescending(p => p.PaidAt).ToList();

        var dto = new SupplierDto(s.Id, s.Name, s.Category, s.ContactName, s.Phone, s.Email, s.Address, s.TaxId, s.Balance);
        return new SupplierStatement(dto, live.Sum(o => o.TotalAmount), live.Sum(o => o.AmountPaid), s.Balance, live.Count,
            live.Count(o => o.PaymentStatus != PaymentStatuses.Paid), live.Count > 0 ? live.Max(o => o.OrderDate) : null, items, orderRows, payments);
    }
}
