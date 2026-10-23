using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Inventory.Application.Abstractions;

/// <summary>The company-scoped database (one MySQL schema per company).</summary>
public interface IBusinessDbContext
{
    DbSet<Category> Categories { get; }
    DbSet<Warehouse> Warehouses { get; }
    DbSet<Branch> Branches { get; }
    DbSet<ChatEvent> ChatEvents { get; }
    DbSet<SiteReview> SiteReviews { get; }
    DbSet<SitePartner> SitePartners { get; }
    DbSet<TradePartner> TradePartners { get; }
    DbSet<TradePartnerDocument> TradePartnerDocuments { get; }
    DbSet<WalletEntry> WalletEntries { get; }
    DbSet<TradeApplicationDraft> TradeApplicationDrafts { get; }
    DbSet<TradeApplicationFile> TradeApplicationFiles { get; }
    DbSet<EmailCode> EmailCodes { get; }
    DbSet<GlAccount> GlAccounts { get; }
    DbSet<GlPeriod> GlPeriods { get; }
    DbSet<GlJournal> GlJournals { get; }
    DbSet<GlLine> GlLines { get; }
    DbSet<ApprovalRule> ApprovalRules { get; }
    DbSet<ApprovalRequest> ApprovalRequests { get; }
    DbSet<TaxFiling> TaxFilings { get; }
    DbSet<BankAccount> BankAccounts { get; }
    DbSet<BankStatementLine> BankStatementLines { get; }
    DbSet<BankReconciliation> BankReconciliations { get; }
    DbSet<FixedAsset> FixedAssets { get; }
    DbSet<AssetDepreciation> AssetDepreciations { get; }
    DbSet<DepreciationRun> DepreciationRuns { get; }
    DbSet<Budget> Budgets { get; }
    DbSet<BudgetLine> BudgetLines { get; }
    DbSet<Currency> Currencies { get; }
    DbSet<ExchangeRate> ExchangeRates { get; }
    DbSet<FxDocument> FxDocuments { get; }
    DbSet<FxSettlement> FxSettlements { get; }
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
    DbSet<DeliveryZone> DeliveryZones { get; }
    DbSet<ChatConversation> ChatConversations { get; }
    DbSet<ChatLogMessage> ChatMessages { get; }
    DbSet<Campaign> Campaigns { get; }
    DbSet<CampaignMessage> CampaignMessages { get; }
    DbSet<AppRecord> AppRecords { get; }
    DbSet<CampaignRecipient> CampaignRecipients { get; }
    DbSet<LeaveType> LeaveTypes { get; }
    DbSet<LeaveRequest> LeaveRequests { get; }
    DbSet<InboundMessage> InboundMessages { get; }
    DbSet<SupplierItem> SupplierItems { get; }
    DbSet<ExpenseCategory> ExpenseCategories { get; }
    DbSet<ProductImage> ProductImages { get; }
    DbSet<SupplierPayment> SupplierPayments { get; }
    DbSet<SupplierProduct> SupplierProducts { get; }
    DbSet<Supply> Supplies { get; }
    DbSet<SupplyItem> SupplyItems { get; }

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

/// <param name="BranchId">The branch the user works at in the open business, if they are tied to one (they then use only its warehouses).</param>
public sealed record CurrentUser(int Id, string FullName, string Role, int? BranchId = null);

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
    /// <summary>All the newer features (Accounting, Branches, Time Off, the apps…) are switched on per business; only the demo business has them for now.</summary>
    bool HasNewFeatures { get; }
    /// <summary>Campaigns and the Marketing apps (email, SMS, WhatsApp, automation, social, surveys) only: switched on separately per business (always on with <see cref="HasNewFeatures"/>).</summary>
    bool HasMarketing => HasNewFeatures;
    /// <summary>The warehouse the sales assistant fulfills online orders from (config-set; 0 means unconfigured).</summary>
    int DefaultWarehouseId { get; }
    IReadOnlyList<(string Bank, string AccountName, string AccountNumber)> DefaultBanks { get; }
    /// <summary>Where customers order and pay online (printed on receipts and quotations). Null when the business has no shop site.</summary>
    string? Website => null;
    /// <summary>The business's social accounts, printed as "follow us" links on receipts and quotations.</summary>
    IReadOnlyList<SocialLink> Socials => [];
    /// <summary>Which online processors this business may use ("paystack", "alatpay"); null = every one set up on the server.</summary>
    IReadOnlyList<string>? PaymentProviders => null;
}

/// <param name="Platform">Facebook, Instagram, YouTube, Telegram, X or TikTok.</param>
/// <param name="Handle">What's printed, e.g. "chewypetsfeeds" or "Chewypets TV".</param>
/// <param name="Url">Where the printed link goes.</param>
public sealed record SocialLink(string Platform, string Handle, string Url);

/// <summary>Lets Application code react to provider-specific transient failures without referencing the provider.</summary>
public interface IDbErrorClassifier
{
    bool IsDeadlock(Exception ex);
    bool IsDuplicateKey(Exception ex);
    bool IsForeignKeyViolation(Exception ex);
}
