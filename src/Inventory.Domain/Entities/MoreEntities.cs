namespace Inventory.Domain.Entities;

// Entities for the features beyond the core sales/stock loop. Names and semantics follow the SQL Server source
// (docs/DATABASE_MAPPING.md); times are UTC, DATE columns are DateOnly.

/// <summary>One row per company schema (Id = 1). Replaces the address/phone/bank keys that lived in App.config.</summary>
public class CompanyProfile
{
    public int Id { get; set; } = 1;
    public string LegalName { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string TaxId { get; set; } = "";
    public decimal DefaultVatRate { get; set; } = 7.5m;
    /// <summary>Default rebate percentage for new customers (AppSettings 'rebate.ratePct' in the desktop app).</summary>
    public decimal DefaultRebateRatePct { get; set; } = 1.0m;
    /// <summary>Default rebate in naira per unit bought, for new customers.</summary>
    public decimal DefaultRebatePerUnit { get; set; }
    public List<CompanyBank> Banks { get; set; } = [];
}

public class CompanyBank
{
    public int Id { get; set; }
    public int CompanyProfileId { get; set; } = 1;
    public int SortOrder { get; set; }
    public string BankName { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
}

/// <summary>Company artwork printed on documents: the logo (also the receipt watermark), the receipt stamp/signature, the waybill stamp.</summary>
public class CompanyAsset
{
    public string Kind { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

public static class AssetKinds
{
    public const string Logo = "logo";
    public const string Signature = "signature";
    public const string WaybillStamp = "waybill-stamp";
    public static readonly string[] All = [Logo, Signature, WaybillStamp];
}

/// <summary>A product we buy from a supplier, with the usual cost, so a purchase from that supplier starts from its own list.</summary>
public class SupplierItem
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public int ProductId { get; set; }
    /// <summary>What this supplier usually charges per unit; prefilled on new purchases (0 = not known).</summary>
    public decimal UnitCost { get; set; }
}

/// <summary>
/// Money we paid a supplier. One row per purchase order it went to (a payment that spans several orders is several rows with the same
/// <see cref="Reference"/>), so the supplier's page can show every payment and which bills it settled.
/// </summary>
public class SupplierPayment
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public int PurchaseOrderId { get; set; }
    /// <summary>UTC.</summary>
    public DateTime PaidAt { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Cash";
    /// <summary>Groups the rows of one payment (e.g. "PAY-20260928-0412") so it reads as a single payment on the statement.</summary>
    public string Reference { get; set; } = "";
    public int PaidByUserId { get; set; }
}

/// <summary>
/// What a supplier actually supplied, on its own: quantities, what they charged, what we paid them and what is still owed.
/// Deliberately kept apart from <see cref="PurchaseOrder"/> and from stock — a supply NEVER moves stock, never touches a product's cost
/// price and never enters the ledger, so a supplier's own record stays the single source of truth for what came from them.
/// </summary>
public class Supply
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public DateOnly SupplyDate { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public string PaymentStatus { get; set; } = PaymentStatuses.Unpaid;
    /// <summary>How the last payment was made ("Cash", "Transfer"…) — for the record only; supplies keep no ledger.</summary>
    public string? PaymentMethod { get; set; }
    public string? Note { get; set; }
    public int CreatedByUserId { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }
    public List<SupplyItem> Items { get; set; } = [];
}

/// <summary>
/// One of a supplier's OWN goods, typed in by whoever set the supplier up: their name for it, their pack size, their usual price. Deliberately
/// NOT a <see cref="Product"/> — a supplier's list is their own and has nothing to do with what we hold in stock or sell.
/// </summary>
public class SupplierProduct
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Their pack size as they quote it: "20kg", "50kg", "carton of 12". Free text on purpose.</summary>
    public string? Size { get; set; }
    /// <summary>What one of them is counted as: Bag, Carton, Litre…</summary>
    public string Unit { get; set; } = "Bag";
    /// <summary>What they usually charge for one. Prefills the price when recording a supply; 0 = not known.</summary>
    public decimal UnitCost { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A line on a supply record. It points at the supplier's own item, and also keeps that item's name, size and unit as they were at the time,
/// so renaming or removing an item later never rewrites what history says was supplied.
/// </summary>
public class SupplyItem
{
    public int Id { get; set; }
    public int SupplyId { get; set; }
    public int? SupplierProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Size { get; set; }
    public string Unit { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}

/// <summary>A product's photo, printed on the catalog / price list sent to customers. One per product.</summary>
public class ProductImage
{
    public int ProductId { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

/// <summary>An expense category the business added itself (e.g. "Fuel"), on top of the built-in ones.</summary>
public class ExpenseCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public static class QuotationStatuses
{
    public const string Open = "Open";
    public const string Converted = "Converted";
}

/// <summary>A price computation that never touches stock, ledger or balance until it is converted to a real sale (rule Q1).</summary>
public class Quotation
{
    public int Id { get; set; }
    public string QuotationNumber { get; set; } = "";
    public int CustomerId { get; set; }
    public DateOnly QuotationDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string PriceTier { get; set; } = PriceTiers.Retailer;
    public string Status { get; set; } = QuotationStatuses.Open;
    public int? ConvertedInvoiceId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>The warehouse to fulfill from once converted — set by the sales assistant; a human converting manually still picks one explicitly.</summary>
    public int? WarehouseId { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? DeliveryZone { get; set; }
    /// <summary>Added to the total after VAT (delivery isn't taxed as goods). Zero for pickup or when no fee applies.</summary>
    public decimal DeliveryFee { get; set; }
    public List<QuotationItem> Items { get; set; } = [];
}

public class QuotationItem
{
    public int Id { get; set; }
    public int QuotationId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

/// <summary>A delivery note for an invoice.</summary>
public class Waybill
{
    public int Id { get; set; }
    public string WaybillNumber { get; set; } = "";
    public int InvoiceId { get; set; }
    public DateOnly IssueDate { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPhone { get; set; }
    public string? VehiclePlate { get; set; }
    public string? DestinationAddress { get; set; }
    public string? Notes { get; set; }
    public int? CreatedByUserId { get; set; }
}

public static class AttendanceEventTypes
{
    public const string In = "In";
    public const string Out = "Out";
    public const string Declined = "Declined";
}

/// <summary>Append-only: one row per check-in, check-out or declined check-in (never one row per day).</summary>
public class AttendanceEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string FullName { get; set; } = "";
    public string EventType { get; set; } = AttendanceEventTypes.In;
    /// <summary>UTC.</summary>
    public DateTime HappenedAt { get; set; }
    /// <summary>The Lagos calendar day of <see cref="HappenedAt"/>, stored (MySQL cannot derive it deterministically).</summary>
    public DateOnly WorkDate { get; set; }
}

public class Employee
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Position { get; set; } = "";
    public string? Phone { get; set; }
    public DateOnly StartedOn { get; set; }
    public decimal MonthlySalary { get; set; }
    public bool IsActive { get; set; } = true;
}

public class EmployeeMonthly
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public string Position { get; set; } = "";
    public decimal SalaryAmount { get; set; }
    public decimal LoanDeduction { get; set; }
    public bool Paid { get; set; }
    public DateOnly? PaidDate { get; set; }
    public string? Note { get; set; }
}

public class EmployeeLoan
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly LoanDate { get; set; }
    public decimal Principal { get; set; }
    public decimal Balance { get; set; }
    public string? Note { get; set; }
    public bool Closed { get; set; }
}

public class LoanRepayment
{
    public int Id { get; set; }
    public int LoanId { get; set; }
    public DateOnly PaidDate { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

public static class SerialStatuses
{
    public const string InStock = "In Stock";
    public const string Sold = "Sold";
    public const string Returned = "Returned";
    public const string WrittenOff = "Written Off";
}

/// <summary>One physical serialised unit (scales, feeders) as opposed to loose feed (rule T7).</summary>
public class ProductSerial
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string SerialNumber { get; set; } = "";
    public int? BatchId { get; set; }
    public int? WarehouseId { get; set; }
    public string Status { get; set; } = SerialStatuses.InStock;
    public DateTime ReceivedAt { get; set; }
    public DateTime? SoldAt { get; set; }
    public int? InvoiceId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Price-book history (Candid Purrfect): what a product's three prices were, what they became, who changed them and why.</summary>
public class PriceChange
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public decimal OldDistributor { get; set; }
    public decimal OldWholesaler { get; set; }
    public decimal OldRetail { get; set; }
    public decimal NewDistributor { get; set; }
    public decimal NewWholesaler { get; set; }
    public decimal NewRetail { get; set; }
    public DateTime ChangedAt { get; set; }
    public int? ChangedByUserId { get; set; }
    public string? Note { get; set; }
}

public static class PaymentLinkStatuses
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    /// <summary>Its document was deleted: never settled, even if the customer pays on an old link (they are refunded instead).</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>A place we deliver to (a state, city or area) and what delivery there costs. Managed by an admin; offered by the sales assistant.</summary>
public class DeliveryZone
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Fee { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class PaymentProviders
{
    public const string Paystack = "paystack";
    public const string AlatPay = "alatpay";
    public static readonly string[] All = [Paystack, AlatPay];

    public static bool IsValid(string? p) => p is not null && All.Contains(p);

    /// <summary>How the provider is written on payments, receipts and messages.</summary>
    public static string DisplayName(string provider) => provider switch
    {
        AlatPay => "AlatPay",
        _ => "Paystack",
    };
}

/// <summary>An online-payment link (Paystack or AlatPay) for a quotation or an invoice, at an exact amount. One row per link; a paid row is never changed again.</summary>
public class PaymentLink
{
    public int Id { get; set; }
    /// <summary>Our unique reference; it starts with the company key so a Paystack webhook can be routed to the right business.</summary>
    public string Reference { get; set; } = "";
    public string Provider { get; set; } = PaymentProviders.Paystack;
    /// <summary>The processor's own reference when it issues one (AlatPay does; for Paystack it equals <see cref="Reference"/>).</summary>
    public string? ProviderReference { get; set; }
    public string DocType { get; set; } = "Invoice";
    public int DocId { get; set; }
    public string DocNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Url { get; set; } = "";
    public string Status { get; set; } = PaymentLinkStatuses.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? Channel { get; set; }
    /// <summary>When the admin was told. Null on a paid link means the message failed and someone should check.</summary>
    public DateTime? NotifiedAt { get; set; }
    /// <summary>When the admin got the WhatsApp "order paid" message. Null on a paid link means it wasn't sent.</summary>
    public DateTime? AdminNotifiedAt { get; set; }
    /// <summary>When the dispatch rider got the WhatsApp delivery message. Null on a paid link means it wasn't sent.</summary>
    public DateTime? RiderNotifiedAt { get; set; }
}
