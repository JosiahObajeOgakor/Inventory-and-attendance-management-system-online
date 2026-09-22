using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Inventory.Application.Abstractions;

/// <summary>The company-scoped database (one MySQL schema per company).</summary>
public interface IBusinessDbContext
{
    DbSet<Category> Categories { get; }
    DbSet<Warehouse> Warehouses { get; }
    DbSet<Product> Products { get; }
    DbSet<StockBatch> StockBatches { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderItem> PurchaseOrderItems { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceItem> InvoiceItems { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PriceOverride> PriceOverrides { get; }
    DbSet<RebateEntry> RebateEntries { get; }
    DbSet<LedgerEntry> Ledger { get; }
    DbSet<Expense> Expenses { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<CompanyProfile> CompanyProfiles { get; }
    DbSet<CompanyBank> CompanyBanks { get; }
    DbSet<CompanyAsset> CompanyAssets { get; }
    DbSet<Quotation> Quotations { get; }
    DbSet<QuotationItem> QuotationItems { get; }
    DbSet<Waybill> Waybills { get; }
    DbSet<AttendanceEvent> AttendanceEvents { get; }
    DbSet<Employee> Employees { get; }
    DbSet<EmployeeMonthly> EmployeeMonthlies { get; }
    DbSet<EmployeeLoan> EmployeeLoans { get; }
    DbSet<LoanRepayment> LoanRepayments { get; }
    DbSet<ProductSerial> ProductSerials { get; }
    DbSet<PriceChange> PriceChanges { get; }
    DbSet<PaymentLink> PaymentLinks { get; }

    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    void ClearTracker();
}

public interface IClock
{
    DateTime UtcNow { get; }
    /// <summary>Today's date in the business time zone (Africa/Lagos, UTC+1, no DST), not the server's.</summary>
    DateOnly BusinessToday { get; }
    /// <summary>Current wall-clock time in the business time zone (used in document numbers).</summary>
    DateTime BusinessNow { get; }
}

public sealed record CurrentUser(int Id, string FullName, string Role);

public interface ICurrentUser
{
    CurrentUser? User { get; }
}

public interface ICompanyContext
{
    string Key { get; }
    /// <summary>Start of every document number, e.g. "ChewyStock" or "CandidPurrfect".</summary>
    string DocumentPrefix { get; }
    /// <summary>Registered name printed on documents (from configuration; the company profile can override it).</summary>
    string LegalName { get; }
    /// <summary>Candid Purrfect keeps a price book and sends price lists; ChewyPets does not.</summary>
    bool HasPriceLists { get; }
    /// <summary>Candid buys what it sells (no production runs); ChewyPets produces.</summary>
    bool BuysGoods { get; }
    IReadOnlyList<(string Bank, string AccountName, string AccountNumber)> DefaultBanks { get; }
}

/// <summary>Lets Application code react to provider-specific transient failures without referencing the provider.</summary>
public interface IDbErrorClassifier
{
    bool IsDeadlock(Exception ex);
    bool IsDuplicateKey(Exception ex);
    bool IsForeignKeyViolation(Exception ex);
}
