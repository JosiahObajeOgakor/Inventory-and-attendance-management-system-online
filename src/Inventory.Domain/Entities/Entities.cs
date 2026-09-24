namespace Inventory.Domain.Entities;

// Business entities for ONE company (one MySQL schema per company — decision D1).
// User references are plain ints: users live in the shared identity store, not in the company schema.
// Column names/semantics follow the SQL Server source (see docs/DATABASE_MAPPING.md).

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Warehouse
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public string Unit { get; set; } = "Bag";
    public int ReorderLevel { get; set; }
    public decimal CostPrice { get; set; }
    /// <summary>Legacy column, kept equal to <see cref="PriceRetail"/>.</summary>
    public decimal SellingPrice { get; set; }
    public decimal PriceDistributor { get; set; }
    public decimal PriceWholesaler { get; set; }
    public decimal PriceRetail { get; set; }
    public string? Barcode { get; set; }
    public bool TracksSerial { get; set; }
    public bool IsActive { get; set; } = true;

    // Nutrition facts, filled in by an admin when known — used by the sales assistant's product Q&A
    // and nutrition-guidance tool. All optional: the assistant degrades to generic guidance when unset.
    public string? Species { get; set; }
    public string? LifeStage { get; set; }
    public decimal? ProteinPct { get; set; }
    public decimal? FatPct { get; set; }
    public decimal? FiberPct { get; set; }
    public decimal? MoisturePct { get; set; }
    public string? NutritionSummary { get; set; }

    public decimal PriceFor(string tier) => tier switch
    {
        PriceTiers.Distributor => PriceDistributor,
        PriceTiers.Wholesaler => PriceWholesaler,
        _ => PriceRetail,
    };
}

public class StockBatch
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public string BatchNumber { get; set; } = "";
    public DateOnly? ExpiryDate { get; set; }
    public int QuantityOnHand { get; set; }
}

public class StockMovement
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public string MovementType { get; set; } = MovementTypes.In;
    public int Quantity { get; set; }
    public string? ReferenceType { get; set; }
    public int? ReferenceId { get; set; }
    /// <summary>UTC.</summary>
    public DateTime MovementDate { get; set; }
    public int UserId { get; set; }
    /// <summary>The batch a stock-OUT came from, so a void can put units back where they were (keeps expiry).</summary>
    public int? BatchId { get; set; }
    public string? Note { get; set; }
}

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxId { get; set; }
    /// <summary>What we owe this supplier (maintained running total).</summary>
    public decimal Balance { get; set; }
}

public class PurchaseOrder
{
    public int Id { get; set; }
    public string PoNumber { get; set; } = "";
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public DateOnly OrderDate { get; set; }
    public string Status { get; set; } = PurchaseStatuses.Pending;
    public string PaymentStatus { get; set; } = PaymentStatuses.Unpaid;
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public int CreatedByUserId { get; set; }
    public bool IsSample { get; set; }
    public List<PurchaseOrderItem> Items { get; set; } = [];
}

public class PurchaseOrderItem
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public string? Address { get; set; }
    public string? Email { get; set; }
    public string CustomerType { get; set; } = CustomerTypes.Retailer;
    public string? TaxId { get; set; }
    public decimal RebateRatePct { get; set; } = 1.0m;
    public decimal CreditLimit { get; set; }
    /// <summary>What this customer owes (maintained running total).</summary>
    public decimal Balance { get; set; }
}

public class Invoice
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatRate { get; set; } = 7.5m;
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public string Status { get; set; } = PaymentStatuses.Unpaid;
    public decimal AmountPaid { get; set; }
    public DateOnly? DueDate { get; set; }
    public string PriceTier { get; set; } = PriceTiers.Retailer;
    public int? WarehouseId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsSample { get; set; }
    public DateTime? VoidedAt { get; set; }
    public int? VoidedByUserId { get; set; }
    public string? VoidReason { get; set; }
    public List<InvoiceItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public class InvoiceItem
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Products.CostPrice at time of sale — exact COGS.</summary>
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}

public class Payment
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Cash";
    public int ReceivedByUserId { get; set; }
}

/// <summary>Kept denormalised on purpose: survives deletion of the document (S10).</summary>
public class PriceOverride
{
    public int Id { get; set; }
    public string DocType { get; set; } = "Invoice";
    public string DocNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal StandardPrice { get; set; }
    public decimal OverridePrice { get; set; }
    public string ChangedByName { get; set; } = "";
    public DateTime ChangedAt { get; set; }
}

public class RebateEntry
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int? InvoiceId { get; set; }
    public DateOnly EntryDate { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Accrued";
    public DateOnly? RedeemedDate { get; set; }
    public string? Note { get; set; }
}

public class LedgerEntry
{
    public int Id { get; set; }
    public DateOnly EntryDate { get; set; }
    public string AccountType { get; set; } = LedgerAccountTypes.Customer;
    public string AccountName { get; set; } = "";
    /// <summary>New: real links replacing the name-string join.</summary>
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }
    public string EntryType { get; set; } = LedgerEntryTypes.Debit;
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
}

public class Expense
{
    public int Id { get; set; }
    public string Category { get; set; } = "Other";
    public DateOnly ExpenseDate { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public int CreatedByUserId { get; set; }
}

/// <summary>Who did what, to what, when (brief §26). New — the desktop app has no such table.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? EntityId { get; set; }
    public DateTime At { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Replay protection for POST /sales and /purchases.</summary>
public class IdempotencyRecord
{
    public string Key { get; set; } = "";
    public string Scope { get; set; } = "";
    public int? ResultId { get; set; }
    public DateTime CreatedAt { get; set; }
}
