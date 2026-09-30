namespace Inventory.Application.Documents;

public sealed record BankInfo(string Bank, string AccountName, string AccountNumber);

/// <summary>Everything about the business that is printed on a document.</summary>
public sealed record Branding(string CompanyKey, string Name, string Address, string Phone, string Email, string TaxId,
    byte[]? Logo, byte[]? Signature, byte[]? WaybillStamp, IReadOnlyList<BankInfo> Banks,
    string? Website = null, IReadOnlyList<Abstractions.SocialLink>? Socials = null);

public sealed record DocLine(int No, string Sku, string Description, string Unit, int Qty, decimal Price, decimal Amount);

public sealed record PartyInfo(string Name, string Contact, string Address, string Phone, string Email, string TaxId, string Ranking);

public sealed record ReceiptDoc(
    Branding Brand, string Number, DateOnly Date, string Status, DateOnly? DueDate, PartyInfo Customer, string PriceTier, string Warehouse,
    string PaymentMethod, string ServedBy, IReadOnlyList<DocLine> Lines, decimal Subtotal, decimal DiscountPct, decimal DiscountAmount,
    decimal VatRate, decimal VatAmount, decimal Total, decimal Paid, decimal OwedElsewhere, decimal RebateAvailable, DateTime GeneratedAt, string? PayUrl = null,
    string? ScanPayUrl = null)
{
    public decimal BalanceDue => Total - Paid;
}

public sealed record QuotationDoc(
    Branding Brand, string Number, DateOnly Date, string Status, PartyInfo Customer, string PriceTier, string PreparedBy,
    IReadOnlyList<DocLine> Lines, decimal Subtotal, decimal DiscountPct, decimal DiscountAmount, decimal VatRate, decimal VatAmount,
    decimal Total, DateTime GeneratedAt, string? PayUrl = null, string? ScanPayUrl = null);

public sealed record WaybillDoc(
    Branding Brand, string Number, DateOnly IssueDate, string InvoiceNumber, DateOnly InvoiceDate, PartyInfo Customer, string DestinationAddress,
    string FromWarehouse, string IssuedBy, string DriverName, string DriverPhone, string VehiclePlate, string Notes,
    IReadOnlyList<DocLine> Lines, DateTime GeneratedAt);

public sealed record PriceListLine(string Category, string Product, string Sku, string Unit, decimal Price, int InStock, byte[]? Image = null);

/// <summary>A price list for one tier; <c>Catalog</c> = a picture catalog (a card per product with its photo and price) instead of the plain table.</summary>
public sealed record PriceListDoc(Branding Brand, string Tier, string? CustomerName, string Reference, DateOnly Date, IReadOnlyList<PriceListLine> Lines, DateTime GeneratedAt,
    bool Catalog = false);

/// <summary>
/// What we bought from a supplier, sent to them: the goods, what the order comes to, what we've paid on it, and what we still owe them in total.
/// <see cref="VatAmount"/> is whatever the order total carries above its lines (the rate isn't stored on the order).
/// </summary>
public sealed record PurchaseOrderDoc(
    Branding Brand, string Number, DateOnly Date, string Status, string PaymentStatus, PartyInfo Supplier, string PreparedBy,
    IReadOnlyList<DocLine> Lines, decimal Subtotal, decimal VatAmount, decimal Total, decimal Paid, decimal OwedElsewhere, DateTime GeneratedAt)
{
    public decimal BalanceDue => Math.Max(0, Total - Paid);
}

/// <summary>Turns a document model into a PDF. Implemented in Infrastructure so the Application layer has no PDF dependency.</summary>
public interface IDocumentRenderer
{
    byte[] Receipt(ReceiptDoc d);
    byte[] PurchaseOrder(PurchaseOrderDoc d);
    byte[] Quotation(QuotationDoc d);
    byte[] Waybill(WaybillDoc d);
    byte[] PriceList(PriceListDoc d);
    /// <summary>The same price list / catalog as ONE tall PNG image — easy to send on WhatsApp.</summary>
    byte[] PriceListPng(PriceListDoc d);
    /// <summary>A Code 128 label the way the desktop app printed shelf labels: name, SKU, price and a scannable barcode.</summary>
    byte[] ShelfLabel(Branding brand, string productName, string sku, string barcode, decimal price);
}
