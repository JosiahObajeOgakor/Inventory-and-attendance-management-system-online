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
}

/// <summary>An online-payment link (Paystack) for a quotation or an invoice, at an exact amount. One row per link; a paid row is never changed again.</summary>
public class PaymentLink
{
    public int Id { get; set; }
    /// <summary>Unique reference sent to the processor; it starts with the company key so a webhook can be routed to the right business.</summary>
    public string Reference { get; set; } = "";
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
}
