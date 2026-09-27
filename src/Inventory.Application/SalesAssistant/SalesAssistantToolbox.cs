using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Application.Company;
using Inventory.Application.Payments;
using Inventory.Application.Queries;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant;

/// <summary>
/// The sales assistant's tools: unlike the read-only admin AiToolbox, these can actually create a
/// quotation and a payment link. Every mutating call runs as a synthetic system actor (mirrors the
/// Paystack system user in Payments.cs) since there is no signed-in human behind a chat message.
/// The model is NEVER trusted with a price — create_quotation only ever takes a productId and quantity;
/// the unit price is always resolved server-side from the product's own retail price.
/// </summary>
public sealed class SalesAssistantToolbox(
    IBusinessDbContext db, CatalogQueries catalog, CustomerLookupService customers, QuotationService quotations,
    PaymentLinkService pay, ChatConversationService conversations, VetGuidanceService vet, CompanyProfileService profile,
    ICompanyContext company, DeliveryZoneService zones, OrderDispatchService dispatch)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly CurrentUser SalesBot = new(0, "Sales Assistant", "SYSTEM");

    public static readonly IReadOnlyList<ToolSpec> Specs =
    [
        new("search_products", "Search the product catalog by name. Returns id, name, unit, retail price and stock quantity for matches.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Search text, e.g. a product name (optional — omit to list a few products)"}}}"""),
        new("get_product_price", "Exact retail price, stock quantity and nutrition facts (if known) for one product by id.",
            """{"type":"object","properties":{"productId":{"type":"integer","description":"Product id from search_products"}},"required":["productId"]}"""),
        new("list_delivery_zones", "The delivery areas we deliver to and the delivery fee for each. Call this before creating a quotation so the customer can pick their area.",
            "{\"type\":\"object\",\"properties\":{}}"),
        new("create_quotation", "Create a priced order (quotation) from real products at their real retail price, plus the delivery fee for the customer's area. Needs the customer's name, delivery address and delivery zone (from list_delivery_zones) — ask for any you don't have. On WhatsApp the phone number is already known; on web chat ask for it. Replaces any order already open in this conversation.",
            """{"type":"object","properties":{"customerName":{"type":"string","description":"Customer's full name"},"phone":{"type":"string","description":"Customer's phone number with country code (web chat only; omit on WhatsApp)"},"deliveryAddress":{"type":"string","description":"Full delivery address: street, area, landmark"},"deliveryZone":{"type":"string","description":"Exact zone name from list_delivery_zones"},"lines":{"type":"array","description":"Products to order","items":{"type":"object","properties":{"productId":{"type":"integer","description":"Product id from search_products"},"quantity":{"type":"integer","description":"How many units"}},"required":["productId","quantity"]}}},"required":["customerName","deliveryAddress","deliveryZone","lines"]}"""),
        new("get_checkout_link", "Get (or reuse) the payment link for the order open in this conversation, on the payment option the customer chose. If they haven't chosen yet, ask: Paystack or AlatPay.",
            """{"type":"object","properties":{"provider":{"type":"string","enum":["paystack","alatpay"],"description":"The payment option the customer chose"}}}"""),
        new("check_order_status", "Check whether the order open in this conversation has been paid. Also re-checks with the payment processor, so use it when the customer says they've paid.", "{\"type\":\"object\",\"properties\":{}}"),
        new("dog_nutrition_guidance", "General nutrition/feeding guidance for a dog or cat, tied to a product's real stats when relevant. NOT for diagnosing illness or medication — always redirects real symptoms to a vet. Use this for ANY health/nutrition question instead of answering it yourself.",
            """{"type":"object","properties":{"question":{"type":"string","description":"The customer's question, as asked"},"productId":{"type":"integer","description":"A relevant product id, if one was discussed (optional)"}},"required":["question"]}"""),
    ];

    public async Task<string> ExecuteAsync(int conversationId, string name, string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var a = doc.RootElement;
            string? Str(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? IntOrNull(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : null;

            object result = name switch
            {
                "search_products" => await SearchProducts(Str("query"), ct),
                "get_product_price" => IntOrNull("productId") is int pid ? await GetProduct(pid, ct) : new { error = "productId is required." },
                "list_delivery_zones" => await ListZones(ct),
                "create_quotation" => await CreateQuotation(conversationId, a, ct),
                "get_checkout_link" => await GetCheckoutLink(conversationId, Str("provider"), ct),
                "check_order_status" => await CheckOrderStatus(conversationId, ct),
                "dog_nutrition_guidance" => new { answer = await vet.AskAsync(Str("question") ?? "", IntOrNull("productId"), ct) },
                _ => new { error = "Unknown tool." },
            };
            return JsonSerializer.Serialize(result, Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new { error = "Could not read that." });
        }
    }

    private async Task<object> SearchProducts(string? query, CancellationToken ct)
    {
        var page = await catalog.ProductsAsync(new PageRequest(1, 10, query), includeInactive: false, isAdmin: false, ct);
        return page.Items.Select(p => new { p.Id, p.Name, p.Unit, price = p.PriceRetail, inStock = p.TotalQuantity }).ToList();
    }

    private async Task<object> GetProduct(int productId, CancellationToken ct)
    {
        var p = await catalog.ProductAsync(productId, isAdmin: false, ct);
        if (p is null) return new { error = "No such product." };
        return new { p.Id, p.Name, p.Unit, price = p.PriceRetail, inStock = p.TotalQuantity, p.Species, p.LifeStage, p.ProteinPct, p.FatPct, p.NutritionSummary };
    }

    private async Task<object> ListZones(CancellationToken ct)
    {
        var list = await zones.ListAsync(activeOnly: true, ct);
        if (list.Count == 0) return new { zones = Array.Empty<object>(), note = "No delivery areas are set up yet: create the order without a zone and tell the customer the team will confirm delivery." };
        return new { zones = list.Select(z => new { name = z.Name, fee = z.Fee }) };
    }

    private static string? Text(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    private async Task<object> CreateQuotation(int conversationId, JsonElement a, CancellationToken ct)
    {
        var conv = await db.ChatConversations.AsNoTracking().SingleAsync(c => c.Id == conversationId, ct);
        var customerName = Text(a, "customerName");
        // On WhatsApp the sender's number IS the customer's phone — never trust the model to retype it.
        var phone = conv.Channel == ChatChannels.WhatsApp ? conv.ExternalId : Text(a, "phone");
        if (string.IsNullOrWhiteSpace(customerName) || string.IsNullOrWhiteSpace(phone))
            return new { error = conv.Channel == ChatChannels.WhatsApp ? "customerName is required." : "customerName and phone are both required before an order can be created." };

        var address = Text(a, "deliveryAddress");
        var active = await zones.ListAsync(activeOnly: true, ct);
        var zone = await zones.FindActiveAsync(Text(a, "deliveryZone"), ct);
        if (active.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(address) || address.Length < 6) return new { error = "Ask for the full delivery address (street, area, landmark) first." };
            if (zone is null) return new { error = "Unknown delivery zone. Use one of these exact names.", zones = active.Select(z => z.Name) };
        }
        if (address is { Length: > 250 }) address = address[..250];
        if (!a.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array || linesEl.GetArrayLength() == 0)
            return new { error = "Add at least one product line (productId, quantity)." };

        var requested = new List<(int ProductId, int Quantity)>();
        foreach (var l in linesEl.EnumerateArray())
        {
            if (!l.TryGetProperty("productId", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;
            var qty = l.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) ? q : 1;
            if (pid > 0 && qty > 0) requested.Add((pid, qty));
        }
        if (requested.Count == 0) return new { error = "No valid product lines given." };

        var ids = requested.Select(r => r.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id, ct);
        var missing = ids.Where(id => !products.ContainsKey(id)).ToList();
        if (missing.Count > 0) return new { error = $"Product id(s) {string.Join(", ", missing)} not found or inactive. Search again." };

        var customer = await customers.FindOrCreateAsync(phone!, customerName, ct);
        if (!string.IsNullOrWhiteSpace(address) && customer.Address != address)
        {
            customer.Address = address;   // keep the latest delivery address on file for the next order and the waybill
            await db.SaveChangesAsync(ct);
        }
        var companyVat = (await profile.GetAsync(ct)).DefaultVatRate;
        var req = new QuoteRequest
        {
            CustomerId = customer.Id,
            PriceTier = PriceTiers.Retailer,
            DiscountPct = 0,
            VatRate = companyVat,
            DeliveryFee = zone?.Fee ?? 0m,
            DeliveryZone = zone?.Name,
            DeliveryAddress = address,
            WarehouseId = company.DefaultWarehouseId > 0 ? company.DefaultWarehouseId : null,
            Lines = requested.Select(r => new SaleLineDto { ProductId = r.ProductId, Quantity = r.Quantity, UnitPrice = products[r.ProductId].PriceFor(PriceTiers.Retailer) }).ToList(),
        };
        var quote = await quotations.CreateAsync(req, SalesBot, ct);
        await conversations.SetQuotationAsync(conversationId, quote.Id, ct);
        await conversations.LinkCustomerAsync(conversationId, customer.Id, ct);
        return new
        {
            quote.Number, subtotal = quote.Subtotal, vat = quote.VatAmount, deliveryFee = req.DeliveryFee, deliveryZone = req.DeliveryZone, total = quote.Total,
            lines = requested.Select(r => new { product = products[r.ProductId].Name, r.Quantity, unitPrice = products[r.ProductId].PriceFor(PriceTiers.Retailer) }),
            // Seeing this, the chat channel offers the customer a tap-to-choose between the payment options.
            paymentOptions = pay.Providers,
        };
    }

    private async Task<object> GetCheckoutLink(int conversationId, string? provider, CancellationToken ct)
    {
        var options = pay.Providers;
        if (options.Count == 0) return new { error = "Online payment isn't set up yet — tell the customer we'll confirm payment another way." };
        provider = provider?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider))
        {
            if (options.Count > 1) return new { needsChoice = true, paymentOptions = options, instruction = "Ask the customer which they want to pay with: Paystack or AlatPay." };
            provider = options[0];
        }
        if (!options.Contains(provider)) return new { error = $"That payment option isn't available. Available: {string.Join(", ", options.Select(PaymentProviders.DisplayName))}." };
        return await CheckoutLinkForAsync(conversationId, provider, ct);
    }

    /// <summary>The payment link for this conversation's open order on one provider. Also used when a customer taps a payment button.</summary>
    public async Task<object> CheckoutLinkForAsync(int conversationId, string provider, CancellationToken ct)
    {
        var conv = await db.ChatConversations.AsNoTracking().SingleAsync(c => c.Id == conversationId, ct);
        if (conv.QuotationId is not int qid) return new { error = "No order has been created in this conversation yet — create one first." };
        var quote = await quotations.GetAsync(qid, ct);
        if (quote is null) return new { error = "That order no longer exists." };
        if (quote.Status != QuotationStatuses.Open) return new { error = "This order has already been paid." };
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == quote.CustomerId, ct);
        var link = await pay.GetOrCreateAsync(PaymentDocTypes.Quotation, quote.Id, quote.QuotationNumber, quote.TotalAmount, customer.Email, customer.Name, provider, ct);
        if (link is null) return new { error = "That payment option isn't available right now — offer the other one." };
        return new { checkoutUrl = link.Url, amount = link.Amount, quotationNumber = quote.QuotationNumber, provider = PaymentProviders.DisplayName(link.Provider) };
    }

    private async Task<object> CheckOrderStatus(int conversationId, CancellationToken ct)
    {
        var conv = await db.ChatConversations.AsNoTracking().SingleAsync(c => c.Id == conversationId, ct);
        if (conv.QuotationId is not int qid) return new { error = "No order has been started in this conversation yet." };
        var link = await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == qid).OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (link is null) return new { status = "no payment link yet" };
        if (link.Status == PaymentLinkStatuses.Pending)
        {
            // The customer says they've paid but no webhook has arrived yet: ask the processor directly.
            var settled = await pay.SettleDocumentAsync(PaymentDocTypes.Quotation, qid, ct);
            if (settled is not null)
            {
                await dispatch.CompleteAsync(settled, ct);
                return new { status = PaymentLinkStatuses.Paid, note = "Payment confirmed just now. Receipt sent; the order is being prepared for delivery." };
            }
            return new { status = PaymentLinkStatuses.Pending, note = "The payment processor hasn't confirmed this payment yet." };
        }
        return new { status = link.Status, paidAmount = link.PaidAmount, paidAt = link.PaidAt, paidWith = PaymentProviders.DisplayName(link.Provider) };
    }
}
