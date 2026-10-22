using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Payments;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain;

namespace Inventory.Application.Website;

public sealed record CheckoutLine(int ProductId, int Quantity);
/// <param name="Zone">A delivery zone's name, or null to collect / arrange delivery separately.</param>
public sealed record CheckoutRequest(string Name, string Phone, string? Email, string? Zone, string? Address, List<CheckoutLine> Lines);
/// <param name="Url">The secure AlatPay page (card, transfer or USSD).</param>
public sealed record CheckoutResult(string Url, string Number, decimal Total);

/// <summary>
/// Pay on the website: the basket becomes a quotation at today's retail prices (never a price the browser sent), and the customer is sent to a secure
/// AlatPay page for exactly that total. When AlatPay confirms, the existing payment flow turns the quotation into a sale, deducts the stock and sends the
/// receipt and alerts, the same as a WhatsApp order.
/// </summary>
public sealed class WebCheckoutService(CustomerLookupService customers, QuotationService quotations, PaymentLinkService pay, StockAvailabilityService stock,
    CompanyProfileService profile, DeliveryZoneService zones, WebsiteService site, IBusinessDbContext db)
{
    private static readonly CurrentUser WebShop = new(0, "Website shop", "SYSTEM");

    public async Task<CheckoutResult> CheckoutAsync(CheckoutRequest r, string? returnUrl, CancellationToken ct)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 80) throw new BusinessRuleException("Please enter your name.");
        var phone = ChatFlowService.ParsePhone(r.Phone) ?? throw new BusinessRuleException("Please enter your phone number, e.g. 0803 123 4567.");
        var email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        if (email is not null && (email.Length > 120 || ChatLexicon.ExtractEmail(email) != email)) throw new BusinessRuleException("That email address doesn't look right.");
        var lines = (r.Lines ?? []).Where(l => l.Quantity > 0).GroupBy(l => l.ProductId).Select(g => (ProductId: g.Key, Qty: g.Sum(l => l.Quantity))).ToList();
        if (lines.Count == 0) throw new BusinessRuleException("Your basket is empty.");
        if (lines.Count > 30 || lines.Any(l => l.Qty > 999)) throw new BusinessRuleException("That basket is too large to pay online. Please order on WhatsApp.");
        if (!pay.Enabled) throw new BusinessRuleException("Paying online isn't available right now. Please send your basket on WhatsApp instead.");

        // Prices and availability from the stock system, never from the browser.
        var cat = (await stock.CatalogueAsync(null, PriceTiers.Retailer, ct)).ToDictionary(c => c.Id);
        foreach (var l in lines)
        {
            if (!cat.TryGetValue(l.ProductId, out var item) || item.Price <= 0) throw new BusinessRuleException("An item in your basket is no longer sold. Please refresh the page.");
            if (item.Available < l.Qty) throw new BusinessRuleException(item.Available <= 0 ? $"{item.Name} has just sold out." : $"Only {item.Available} × {item.Name} left. Please lower the quantity.");
        }
        DeliveryZoneDto? zone = null;
        if (!string.IsNullOrWhiteSpace(r.Zone))
            zone = await zones.FindActiveAsync(r.Zone, ct) ?? throw new BusinessRuleException("Choose a delivery area from the list.");
        var address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim()[..Math.Min(250, r.Address.Trim().Length)];
        if (zone is not null && address is null) throw new BusinessRuleException("Please enter the delivery address.");

        var customer = await customers.FindOrCreateAsync(phone, name, ct);
        if (customer.Name == phone) customer.Name = name;
        if (email is not null && string.IsNullOrWhiteSpace(customer.Email)) customer.Email = email;
        await db.SaveChangesAsync(ct);

        var quote = await quotations.CreateAsync(new QuoteRequest
        {
            CustomerId = customer.Id, PriceTier = PriceTiers.Retailer, VatRate = (await profile.GetAsync(ct)).DefaultVatRate,
            DeliveryFee = zone?.Fee ?? 0, DeliveryZone = zone?.Name, DeliveryAddress = address,
            Lines = lines.Select(l => new SaleLineDto { ProductId = l.ProductId, Quantity = l.Qty, UnitPrice = cat[l.ProductId].Price }).ToList(),
        }, WebShop, ct);
        var link = await pay.GetOrCreateAsync(PaymentDocTypes.Quotation, quote.Id, quote.Number, quote.Total, email ?? customer.Email, customer.Name, pay.Providers.FirstOrDefault(), ct, returnUrl)
            ?? throw new BusinessRuleException("We couldn't open the payment page just now. Please send your basket on WhatsApp instead.");
        await site.TrackAsync(SiteEvents.Checkout, quote.Total, ct);
        return new CheckoutResult(link.Url, quote.Number, quote.Total);
    }
}
