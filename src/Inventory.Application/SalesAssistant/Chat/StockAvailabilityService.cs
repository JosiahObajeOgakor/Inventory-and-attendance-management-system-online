using Inventory.Application.Abstractions;
using Inventory.Application.Payments;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// The chat's only source of truth for "can I sell this": units on hand in ALL the business's warehouses and branches (unexpired batches: a sale takes from the default warehouse first and then from any other),
/// MINUS units promised to other customers who were handed a payment account and haven't paid yet. Without that second part two customers could be
/// quoted the last bag, both pay, and one sale fails after the money has moved.
/// </summary>
public sealed class StockAvailabilityService(IBusinessDbContext db, ICompanyContext company, IClock clock, IOptions<SalesAssistantOptions> options)
{
    /// <summary>Active, non-serial products with their price and reservation-adjusted stock. <paramref name="ownQuotationId"/> is the shopper's own open order, which must not count against them.</summary>
    public Task<List<CatalogItem>> CatalogueAsync(int? ownQuotationId, CancellationToken ct) => CatalogueAsync(ownQuotationId, PriceTiers.Retailer, ct);

    /// <summary>
    /// The same catalogue priced for one customer's tier: an approved trade partner sees their own prices, anyone else the retail price.
    /// A product without a price for that tier is left out altogether: the chat and the shop must never offer something at ₦0.
    /// </summary>
    public async Task<List<CatalogItem>> CatalogueAsync(int? ownQuotationId, string tier, CancellationToken ct)
    {
        var products = (await db.Products.AsNoTracking().Where(p => p.IsActive && !p.TracksSerial).OrderBy(p => p.Name).ToListAsync(ct))
            .Where(p => p.PriceFor(tier) > 0).ToList();
        if (products.Count == 0) return [];
        var ids = products.Select(p => p.Id).ToList();
        var available = await AvailableAsync(ids, ownQuotationId, ct);
        return products.Select(p => new CatalogItem(p.Id, p.Name, p.Unit, p.PriceFor(tier), available.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<Dictionary<int, int>> AvailableAsync(IReadOnlyCollection<int> productIds, int? ownQuotationId, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var now = clock.UtcNow;
        var holdFrom = now.AddMinutes(-options.Value.ReservationMinutes);

        var onHand = await db.StockBatches.AsNoTracking()
            .Where(b => productIds.Contains(b.ProductId) && b.QuantityOnHand > 0 && (b.ExpiryDate == null || b.ExpiryDate >= today))
            .GroupBy(b => b.ProductId).Select(g => new { g.Key, Qty = g.Sum(b => b.QuantityOnHand) }).ToListAsync(ct);

        var held = db.PaymentLinks.AsNoTracking()
            .Where(l => l.Status == PaymentLinkStatuses.Pending && l.DocType == PaymentDocTypes.Quotation
                        && l.CreatedAt > holdFrom && (l.ExpiresAt == null || l.ExpiresAt > now))
            .Select(l => l.DocId);
        var reserved = await db.QuotationItems.AsNoTracking()
            .Where(i => productIds.Contains(i.ProductId) && held.Contains(i.QuotationId) && (ownQuotationId == null || i.QuotationId != ownQuotationId))
            .GroupBy(i => i.ProductId).Select(g => new { g.Key, Qty = g.Sum(i => i.Quantity) }).ToListAsync(ct);

        var result = productIds.ToDictionary(id => id, _ => 0);
        foreach (var o in onHand) result[o.Key] = o.Qty;
        foreach (var r in reserved) result[r.Key] = Math.Max(0, result[r.Key] - r.Qty);
        return result;
    }
}
