using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Purchasing;

/// <summary>One of a supplier's own items. <paramref name="Id"/> 0 on the way in means "new".</summary>
public sealed record SupplierProductInput(int Id, string Name, string? Size, string Unit, decimal UnitCost, bool IsActive = true);
public sealed record SupplierProductDto(int Id, string Name, string? Size, string Unit, decimal UnitCost, bool IsActive, int TimesSupplied,
    int QuantitySupplied, decimal AmountSupplied);

/// <summary>
/// Each supplier's own list of goods — their names, their pack sizes, their prices. Nothing here touches products, stock or the catalogue we
/// sell from: it exists so that recording a supply is a matter of ticking what the supplier brought and typing quantities.
/// </summary>
public sealed class SupplierCatalogService(IBusinessDbContext db, TransactionRunner tx, IClock clock)
{
    public async Task<List<SupplierProductDto>> ListAsync(int supplierId, bool includeInactive, CancellationToken ct = default)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId, ct)) throw new NotFoundException("Supplier");
        var items = await db.SupplierProducts.AsNoTracking().Where(p => p.SupplierId == supplierId && (includeInactive || p.IsActive))
            .OrderBy(p => p.Name).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        // What each item has actually brought in, so the list doubles as "how much of this do they supply us".
        var used = await db.SupplyItems.AsNoTracking().Where(i => i.SupplierProductId != null && ids.Contains(i.SupplierProductId!.Value))
            .GroupBy(i => i.SupplierProductId!.Value)
            .Select(g => new { Id = g.Key, Times = g.Count(), Qty = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.LineTotal) })
            .ToDictionaryAsync(x => x.Id, ct);
        return items.Select(p => new SupplierProductDto(p.Id, p.Name, p.Size, p.Unit, p.UnitCost, p.IsActive,
            used.GetValueOrDefault(p.Id)?.Times ?? 0, used.GetValueOrDefault(p.Id)?.Qty ?? 0, used.GetValueOrDefault(p.Id)?.Amount ?? 0)).ToList();
    }

    /// <summary>
    /// Replaces this supplier's list with exactly these items. An item that has already been supplied is never deleted — it is marked inactive
    /// instead, so it drops off the pick list while every past record still points at something real.
    /// </summary>
    public Task<List<SupplierProductDto>> SaveAsync(int supplierId, IReadOnlyList<SupplierProductInput> items, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId, inner)) throw new NotFoundException("Supplier");
            foreach (var i in items)
            {
                if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Every item needs a name.");
                if (i.Name.Trim().Length > 150) throw new BusinessRuleException($"\"{i.Name.Trim()[..40]}…\" is too long a name.");
                if (i.UnitCost < 0) throw new BusinessRuleException("A price can't be negative.");
            }
            var names = items.Select(i => i.Name.Trim().ToLowerInvariant()).ToList();
            if (names.Count != names.Distinct().Count()) throw new BusinessRuleException("The same item is listed twice.");

            var existing = await db.SupplierProducts.Where(p => p.SupplierId == supplierId).ToListAsync(inner);
            var keptIds = items.Where(i => i.Id > 0).Select(i => i.Id).ToHashSet();

            // Gone from the list: delete the ones never supplied, retire the ones that were.
            var supplied = await db.SupplyItems.AsNoTracking().Where(i => i.SupplierProductId != null)
                .Select(i => i.SupplierProductId!.Value).Distinct().ToListAsync(inner);
            foreach (var row in existing.Where(e => !keptIds.Contains(e.Id)))
            {
                if (supplied.Contains(row.Id)) row.IsActive = false;
                else db.SupplierProducts.Remove(row);
            }

            foreach (var i in items)
            {
                var name = i.Name.Trim();
                var size = string.IsNullOrWhiteSpace(i.Size) ? null : i.Size.Trim();
                var unit = string.IsNullOrWhiteSpace(i.Unit) ? "Bag" : i.Unit.Trim();
                var row = i.Id > 0 ? existing.FirstOrDefault(e => e.Id == i.Id) : null;
                if (row is null)
                {
                    db.SupplierProducts.Add(new SupplierProduct
                    {
                        SupplierId = supplierId, Name = name, Size = size, Unit = unit, UnitCost = i.UnitCost, IsActive = true, CreatedAt = clock.UtcNow,
                    });
                }
                else
                {
                    row.Name = name; row.Size = size; row.Unit = unit; row.UnitCost = i.UnitCost; row.IsActive = i.IsActive;
                }
            }

            db.AuditLogs.Add(new AuditLog
            {
                UserId = user.Id, UserName = user.FullName, Action = "SUPPLIER_ITEMS_SAVED", Entity = "Supplier", EntityId = supplierId.ToString(),
                At = clock.UtcNow, Detail = $"{items.Count} item(s) on the supplier's own list",
            });
            await db.SaveChangesAsync(inner);
            return await ListAsync(supplierId, includeInactive: false, inner);
        }, ct);
}
