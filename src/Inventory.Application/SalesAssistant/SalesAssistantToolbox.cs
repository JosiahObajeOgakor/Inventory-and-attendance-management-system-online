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
    PaymentLinkService pay, ChatConversationService conversations, VetGuidanceService vet, CompanyProfileService profile)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly CurrentUser SalesBot = new(0, "Sales Assistant", "SYSTEM");

    public static readonly IReadOnlyList<ToolSpec> Specs =
    [
        new("search_products", "Search the product catalog by name. Returns id, name, unit, retail price and stock quantity for matches.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Search text, e.g. a product name (optional — omit to list a few products)"}}}"""),
        new("get_product_price", "Exact retail price, stock quantity and nutrition facts (if known) for one product by id.",
            """{"type":"object","properties":{"productId":{"type":"integer","description":"Product id from search_products"}},"required":["productId"]}"""),
        new("create_quotation", "Create a priced quotation for the customer from real products at their real retail price. Requires the customer's name and phone number — ask for them first if you don't have them. Replaces any quotation already open in this conversation.",
            """{"type":"object","properties":{"customerName":{"type":"string","description":"Customer's full name"},"phone":{"type":"string","description":"Customer's WhatsApp/phone number, with country code"},"lines":{"type":"array","description":"Products to order","items":{"type":"object","properties":{"productId":{"type":"integer","description":"Product id from search_products"},"quantity":{"type":"integer","description":"How many units"}},"required":["productId","quantity"]}}},"required":["customerName","phone","lines"]}"""),
        new("get_checkout_link", "Get (or reuse) the Paystack payment link for the quotation currently open in this conversation, so the customer can pay.", "{\"type\":\"object\",\"properties\":{}}"),
        new("check_order_status", "Check whether the quotation currently open in this conversation has been paid yet.", "{\"type\":\"object\",\"properties\":{}}"),
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
                "create_quotation" => await CreateQuotation(conversationId, a, ct),
                "get_checkout_link" => await GetCheckoutLink(conversationId, ct),
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

    private async Task<object> CreateQuotation(int conversationId, JsonElement a, CancellationToken ct)
    {
        var customerName = a.TryGetProperty("customerName", out var n) ? n.GetString()?.Trim() : null;
        var phone = a.TryGetProperty("phone", out var ph) ? ph.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(customerName) || string.IsNullOrWhiteSpace(phone))
            return new { error = "customerName and phone are both required before a quotation can be created." };
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
        var companyVat = (await profile.GetAsync(ct)).DefaultVatRate;
        var req = new QuoteRequest
        {
            CustomerId = customer.Id,
            PriceTier = PriceTiers.Retailer,
            DiscountPct = 0,
            VatRate = companyVat,
            Lines = requested.Select(r => new SaleLineDto { ProductId = r.ProductId, Quantity = r.Quantity, UnitPrice = products[r.ProductId].PriceFor(PriceTiers.Retailer) }).ToList(),
        };
        var quote = await quotations.CreateAsync(req, SalesBot, ct);
        await conversations.SetQuotationAsync(conversationId, quote.Id, ct);
        await conversations.LinkCustomerAsync(conversationId, customer.Id, ct);
        return new { quote.Number, subtotal = quote.Subtotal, vat = quote.VatAmount, total = quote.Total,
            lines = requested.Select(r => new { product = products[r.ProductId].Name, r.Quantity, unitPrice = products[r.ProductId].PriceFor(PriceTiers.Retailer) }) };
    }

    private async Task<object> GetCheckoutLink(int conversationId, CancellationToken ct)
    {
        var conv = await db.ChatConversations.AsNoTracking().SingleAsync(c => c.Id == conversationId, ct);
        if (conv.QuotationId is not int qid) return new { error = "No quotation has been created in this conversation yet — create one first." };
        var quote = await quotations.GetAsync(qid, ct);
        if (quote is null) return new { error = "That quotation no longer exists." };
        if (quote.Status != QuotationStatuses.Open) return new { error = "This order has already been paid and converted." };
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == quote.CustomerId, ct);
        var link = await pay.GetOrCreateAsync(PaymentDocTypes.Quotation, quote.Id, quote.QuotationNumber, quote.TotalAmount, customer.Email, customer.Name, ct);
        if (link is null) return new { error = "Online payment isn't set up yet — tell the customer we'll confirm payment another way." };
        return new { checkoutUrl = link.Url, amount = link.Amount, quotationNumber = quote.QuotationNumber };
    }

    private async Task<object> CheckOrderStatus(int conversationId, CancellationToken ct)
    {
        var conv = await db.ChatConversations.AsNoTracking().SingleAsync(c => c.Id == conversationId, ct);
        if (conv.QuotationId is not int qid) return new { error = "No order has been started in this conversation yet." };
        var link = await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == qid).OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (link is null) return new { status = "no payment link yet" };
        return new { status = link.Status, paidAmount = link.PaidAmount, paidAt = link.PaidAt };
    }
}
