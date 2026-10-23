using Inventory.Application.Abstractions;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Partners;

public sealed record PartnerPrice(int Id, string Name, string Unit, decimal Price, decimal Retail, bool InStock);
public sealed record PartnerOrder(string Number, DateOnly Date, decimal Total, decimal Paid, string Status);
public sealed record PartnerAccount(string Store, string StoreName, string BusinessName, string ContactName, string Status, string? Tier, string RequestedTier, string? Note,
    DateTime AppliedAt, decimal Owed, decimal Wallet, IReadOnlyList<PartnerPrice> Prices, IReadOnlyList<PartnerOrder> Orders);

/// <summary>
/// What a signed-in trade partner sees for one store: where their application stands and, once approved, their own prices next to retail, what they owe and
/// their recent orders. Read-only; ordering stays on WhatsApp (which recognises their number) until the wallet and portal ordering arrive.
/// </summary>
public sealed class PartnerDashboard(IBusinessDbContext db, StockAvailabilityService stock, ICompanyContext company)
{
    public async Task<PartnerAccount?> ForEmailAsync(string mail, string storeName, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Email == mail, ct);
        if (p is null) return null;
        var approved = p.Status == PartnerStatuses.Approved && PriceTiers.IsValid(p.ApprovedTier);
        IReadOnlyList<PartnerPrice> prices = [];
        IReadOnlyList<PartnerOrder> orders = [];
        decimal owed = 0, wallet = 0;
        if (approved)
        {
            var retail = (await stock.CatalogueAsync(null, PriceTiers.Retailer, ct)).ToDictionary(i => i.Id, i => i.Price);
            prices = (await stock.CatalogueAsync(null, p.ApprovedTier!, ct))
                .Select(i => new PartnerPrice(i.Id, i.Name, i.Unit, i.Price, retail.GetValueOrDefault(i.Id, i.Price), i.Available > 0)).ToList();
            wallet = await db.WalletEntries.AsNoTracking().Where(w => w.TradePartnerId == p.Id && w.Status == WalletEntryStatuses.Confirmed).SumAsync(w => (decimal?)w.Amount, ct) ?? 0;
        }
        if (p.CustomerId is int cid)
        {
            owed = await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Balance).FirstOrDefaultAsync(ct);
            orders = await db.Invoices.AsNoTracking().Where(i => i.CustomerId == cid && i.VoidedAt == null && !i.IsSample).OrderByDescending(i => i.Id).Take(20)
                .Select(i => new PartnerOrder(i.InvoiceNumber, i.InvoiceDate, i.TotalAmount, i.AmountPaid, i.Status)).ToListAsync(ct);
        }
        return new PartnerAccount(company.Key, storeName, p.BusinessName, p.ContactName, p.Status, p.ApprovedTier, p.RequestedTier,
            p.Status is PartnerStatuses.Rejected or PartnerStatuses.Suspended ? p.DecisionNote : null, p.CreatedAt, owed, wallet, prices, orders);
    }
}
