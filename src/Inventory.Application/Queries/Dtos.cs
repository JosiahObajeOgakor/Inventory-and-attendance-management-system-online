namespace Inventory.Application.Queries;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record PageRequest(int Page = 1, int PageSize = 25, string? Search = null)
{
    public int SafePage => Math.Max(1, Page);
    public int SafeSize => Math.Clamp(PageSize, 1, 200);
    public string? Term => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
}

/// <summary>CostPrice is null for clerks: cost/profit is admin-only and enforced here, not in the UI.</summary>
public sealed record ProductDto(int Id, string Sku, string Name, int CategoryId, string Category, string Unit, int ReorderLevel,
    decimal? CostPrice, decimal PriceDistributor, decimal PriceWholesaler, decimal PriceRetail, string? Barcode, bool TracksSerial,
    bool IsActive, int TotalQuantity, string? Species = null, string? LifeStage = null, decimal? ProteinPct = null,
    decimal? FatPct = null, decimal? FiberPct = null, decimal? MoisturePct = null, string? NutritionSummary = null);

public sealed record CategoryDto(int Id, string Name);
public sealed record WarehouseDto(int Id, string Name, string? Location);

public sealed record BatchRowDto(int BatchId, int ProductId, string Sku, string Product, string Category, int WarehouseId, string Warehouse,
    string BatchNumber, DateOnly? ExpiryDate, int Quantity, int ReorderLevel, string Status);

public sealed record MovementRowDto(int Id, DateTime At, int ProductId, string Product, string Sku, string Warehouse, string Type, int Quantity,
    string? ReferenceType, int? ReferenceId, int UserId, string? User, string? Note);

public sealed record SupplierDto(int Id, string Name, string? Category, string? ContactName, string? Phone, string? Email, string? Address, string? TaxId, decimal Balance);
public sealed record SupplierInput(string Name, string? Category, string? ContactName, string? Phone, string? Email, string? Address, string? TaxId);

public sealed record CustomerDto(int Id, string Name, string CustomerType, string? ContactName, string? Phone, string? Location, string? Address,
    string? Email, string? TaxId, decimal RebateRatePct, decimal CreditLimit, decimal Balance, decimal TrailingTwelveMonthSpend, string Ranking);
public sealed record CustomerLookupDto(int Id, string Name, string CustomerType, string? Phone, decimal Balance);
public sealed record CustomerInput(string Name, string CustomerType, string? ContactName, string? Phone, string? Location, string? Address,
    string? Email, string? TaxId, decimal RebateRatePct, decimal CreditLimit);

public sealed record InvoiceRowDto(int Id, string InvoiceNumber, string Customer, DateOnly InvoiceDate, string PaymentMethod, decimal TotalAmount,
    string Status, decimal? EstProfit);
public sealed record InvoiceItemDto(int ProductId, string Product, string Sku, string Unit, int Quantity, decimal UnitPrice, decimal LineTotal, decimal? UnitCost);
public sealed record PaymentDto(DateTime At, decimal Amount, string Method);
public sealed record InvoiceDetailDto(int Id, string InvoiceNumber, int CustomerId, string Customer, string CustomerType, DateOnly InvoiceDate,
    DateOnly? DueDate, decimal Subtotal, decimal DiscountPct, decimal DiscountAmount, decimal VatRate, decimal VatAmount, decimal TotalAmount,
    decimal AmountPaid, string Status, string PaymentMethod, string PriceTier, int? WarehouseId, string CreatedBy, string? VoidReason,
    IReadOnlyList<InvoiceItemDto> Items, IReadOnlyList<PaymentDto> Payments);

public sealed record PurchaseRowDto(int Id, string PoNumber, string Supplier, DateOnly OrderDate, string Status, string PaymentStatus, decimal TotalAmount, decimal AmountPaid);
public sealed record PurchaseItemDto(int ProductId, string Product, string Sku, int Quantity, decimal UnitCost, decimal LineTotal);
public sealed record PurchaseDetailDto(int Id, string PoNumber, int SupplierId, string Supplier, DateOnly OrderDate, string Status, string PaymentStatus,
    decimal TotalAmount, decimal AmountPaid, IReadOnlyList<PurchaseItemDto> Items);

public sealed record LedgerRowDto(int Id, DateOnly EntryDate, string AccountName, string AccountType, string EntryType, decimal Amount, string? Reference);

public sealed record MonthlyIncomeDto(int Year, int Month, int Invoices, decimal GrossSales, decimal Vat, decimal NetSales, decimal Collected, decimal Outstanding);

public sealed record FinanceSummaryDto(int Year, int Month, decimal Revenue, decimal Discounts, decimal Cogs, decimal GrossProfit, decimal Expenses,
    decimal NetProfit, decimal AccountsPayable, decimal AccountsReceivable, decimal RebatesAvailable);

public sealed record ReorderAdviceDto(int ProductId, string Product, int OnHand, int ReorderLevel, decimal AvgDailyDemand, decimal? DaysOfCover,
    int ReorderPoint, int SuggestedOrderQty, string Urgency, string Summary);

public sealed record DashboardDto(decimal StockValue, int LowStockCount, int TodaysInvoices, IReadOnlyList<InvoiceRowDto> RecentInvoices,
    IReadOnlyList<BatchRowDto> LowStock,
    // Admin-only. Null (not zero) for clerks, so the UI cannot show a number that was never sent.
    decimal? GrossProfitThisMonth, decimal? ExpensesThisMonth, IReadOnlyList<ReorderAdviceDto>? Reorder);

public sealed record AuditRowDto(long Id, DateTime At, string User, string Action, string Entity, string? EntityId, string? Detail);
