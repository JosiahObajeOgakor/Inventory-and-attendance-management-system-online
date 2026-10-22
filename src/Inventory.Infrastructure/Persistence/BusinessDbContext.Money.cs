using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>The books and what hangs off them: ledger, tax filings, bank reconciliation, fixed assets, budgets, currencies and approvals.</summary>
public partial class BusinessDbContext
{
    public DbSet<GlAccount> GlAccounts => Set<GlAccount>();
    public DbSet<GlPeriod> GlPeriods => Set<GlPeriod>();
    public DbSet<GlJournal> GlJournals => Set<GlJournal>();
    public DbSet<GlLine> GlLines => Set<GlLine>();
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<TaxFiling> TaxFilings => Set<TaxFiling>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankStatementLine> BankStatementLines => Set<BankStatementLine>();
    public DbSet<BankReconciliation> BankReconciliations => Set<BankReconciliation>();
    public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
    public DbSet<AssetDepreciation> AssetDepreciations => Set<AssetDepreciation>();
    public DbSet<DepreciationRun> DepreciationRuns => Set<DepreciationRun>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<FxDocument> FxDocuments => Set<FxDocument>();
    public DbSet<FxSettlement> FxSettlements => Set<FxSettlement>();

    private static void ConfigureMoney(ModelBuilder b)
    {
        b.Entity<GlAccount>(e =>
        {
            e.ToTable("gl_accounts", t => t.HasCheckConstraint("CK_gl_accounts_type", "Type IN ('Asset','Liability','Equity','Income','Expense')"));
            e.Property(x => x.Code).HasMaxLength(10).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Type).HasMaxLength(10).IsRequired();
        });
        b.Entity<GlPeriod>(e =>
        {
            e.ToTable("gl_periods", t => t.HasCheckConstraint("CK_gl_periods_status", "Status IN ('Open','SoftClosed','Locked')"));
            e.HasIndex(x => new { x.Year, x.Month }).IsUnique();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.ClosedBy).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(300);
            e.Property(x => x.ClosedAt).HasPrecision(6);
        });
        b.Entity<GlJournal>(e =>
        {
            e.ToTable("gl_journals", t => t.HasCheckConstraint("CK_gl_journals_status", "Status IN ('Draft','Posted','Reversed')"));
            e.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
            e.Property(x => x.SourceId).HasMaxLength(40).IsRequired();
            e.Property(x => x.SourceEvent).HasMaxLength(80).IsRequired();
            e.HasIndex(x => new { x.SourceType, x.SourceId, x.SourceEvent }).IsUnique();   // one business event posts once
            e.Property(x => x.Memo).HasMaxLength(300).IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.PreparedByName).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApprovedByName).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.HasIndex(x => x.Date);
            e.HasIndex(x => new { x.BranchId, x.Date });
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.JournalId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<GlLine>(e =>
        {
            e.ToTable("gl_lines", t => t.HasCheckConstraint("CK_gl_lines_amounts", "Debit >= 0 AND Credit >= 0 AND NOT (Debit > 0 AND Credit > 0)"));
            e.Property(x => x.Debit).HasPrecision(16, 2);
            e.Property(x => x.Credit).HasPrecision(16, 2);
            e.Property(x => x.Note).HasMaxLength(300);
            e.HasOne<GlAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.AccountId, x.JournalId });
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.SupplierId);
        });
        b.Entity<ApprovalRule>(e =>
        {
            e.ToTable("approval_rules");
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Action).IsUnique();
            e.Property(x => x.ThresholdAmount).HasPrecision(14, 2);
        });
        b.Entity<ApprovalRequest>(e =>
        {
            e.ToTable("approval_requests", t => t.HasCheckConstraint("CK_approval_requests_status", "Status IN ('Pending','Executing','Executed','Rejected','Failed')"));
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Summary).HasMaxLength(300).IsRequired();
            e.Property(x => x.Payload).HasColumnType("text").IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.RequestedByName).HasMaxLength(100).IsRequired();
            e.Property(x => x.DecidedByName).HasMaxLength(100);
            e.Property(x => x.DecisionNote).HasMaxLength(500);
            e.Property(x => x.FailureReason).HasMaxLength(1000);
            e.Property(x => x.RequestedAt).HasPrecision(6);
            e.Property(x => x.DecidedAt).HasPrecision(6);
            e.HasIndex(x => new { x.Status, x.Id });
        });
        b.Entity<TaxFiling>(e =>
        {
            e.ToTable("tax_filings", t => t.HasCheckConstraint("CK_tax_filings_kind", "Kind IN ('VAT')"));
            e.Property(x => x.Kind).HasMaxLength(5).IsRequired();
            e.Property(x => x.Authority).HasMaxLength(60).IsRequired();
            e.Property(x => x.TaxDue).HasPrecision(16, 2);
            e.Property(x => x.Credits).HasPrecision(16, 2);
            e.Property(x => x.AmountPaid).HasPrecision(16, 2);
            e.Property(x => x.Reference).HasMaxLength(80);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.HasIndex(x => new { x.Kind, x.Year, x.Month });
        });
        b.Entity<BankAccount>(e =>
        {
            e.ToTable("bank_accounts");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.BankName).HasMaxLength(80).IsRequired();
            e.Property(x => x.AccountNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.GlCode).HasMaxLength(10).IsRequired();
            e.HasIndex(x => x.GlCode).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        });
        b.Entity<BankStatementLine>(e =>
        {
            e.ToTable("bank_statement_lines");
            e.Property(x => x.Description).HasMaxLength(300).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(80);
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.Property(x => x.RunningBalance).HasPrecision(16, 2);
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.ImportBatch).HasMaxLength(12).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.BankAccountId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.BankAccountId, x.Status, x.Date });
            e.HasIndex(x => x.GlLineId).IsUnique();   // one ledger line is cleared by at most one statement line (NULLs allowed)
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<GlLine>().WithMany().HasForeignKey(x => x.GlLineId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BankReconciliation>(e =>
        {
            e.ToTable("bank_reconciliations");
            e.Property(x => x.StatementBalance).HasPrecision(16, 2);
            e.Property(x => x.LedgerBalance).HasPrecision(16, 2);
            e.Property(x => x.UnclearedReceipts).HasPrecision(16, 2);
            e.Property(x => x.UnclearedPayments).HasPrecision(16, 2);
            e.Property(x => x.Status).HasMaxLength(12).IsRequired();
            e.Property(x => x.CompletedByName).HasMaxLength(120);
            e.Property(x => x.CompletedAt).HasPrecision(6);
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<FixedAsset>(e =>
        {
            e.ToTable("fixed_assets", t => t.HasCheckConstraint("CK_fixed_assets_cost", "Cost > 0 AND ResidualValue >= 0 AND AccumulatedDepreciation >= 0"));
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Category).HasMaxLength(40).IsRequired();
            e.Property(x => x.SerialNumber).HasMaxLength(80);
            e.Property(x => x.Location).HasMaxLength(120);
            e.Property(x => x.Method).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Cost).HasPrecision(16, 2);
            e.Property(x => x.ResidualValue).HasPrecision(16, 2);
            e.Property(x => x.AccumulatedDepreciation).HasPrecision(16, 2);
            e.Property(x => x.DisposalProceeds).HasPrecision(16, 2);
            e.Property(x => x.DisposalGainLoss).HasPrecision(16, 2);
            e.Property(x => x.RatePct).HasPrecision(6, 3);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Ignore(x => x.BookValue);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AssetDepreciation>(e =>
        {
            e.ToTable("asset_depreciation");
            e.HasIndex(x => new { x.AssetId, x.Year, x.Month }).IsUnique();
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.HasOne<FixedAsset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DepreciationRun>(e =>
        {
            e.ToTable("depreciation_runs");
            e.HasIndex(x => new { x.Year, x.Month }).IsUnique();
            e.Property(x => x.Total).HasPrecision(16, 2);
            e.Property(x => x.RunAt).HasPrecision(6);
        });
        b.Entity<Budget>(e =>
        {
            e.ToTable("budgets");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.ApprovedByName).HasMaxLength(120);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.ApprovedAt).HasPrecision(6);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.BudgetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BudgetLine>(e =>
        {
            e.ToTable("budget_lines", t => t.HasCheckConstraint("CK_budget_lines_month", "Month BETWEEN 1 AND 12"));
            e.Property(x => x.AccountCode).HasMaxLength(10).IsRequired();
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.HasIndex(x => new { x.BudgetId, x.AccountCode, x.Month }).IsUnique();
        });
        b.Entity<Currency>(e =>
        {
            e.ToTable("currencies");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(3);
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
            e.Property(x => x.Symbol).HasMaxLength(8).IsRequired();
        });
        b.Entity<ExchangeRate>(e =>
        {
            e.ToTable("exchange_rates", t => t.HasCheckConstraint("CK_exchange_rates_rate", "Rate > 0"));
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Rate).HasPrecision(18, 6);
            e.Property(x => x.Source).HasMaxLength(60);
            e.HasIndex(x => new { x.Currency, x.Date }).IsUnique();
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.Currency).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<FxDocument>(e =>
        {
            e.ToTable("fx_documents");
            e.Property(x => x.Number).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Description).HasMaxLength(250).IsRequired();
            e.Property(x => x.AccountCode).HasMaxLength(10).IsRequired();
            e.Property(x => x.ForeignAmount).HasPrecision(18, 2);
            e.Property(x => x.ForeignSettled).HasPrecision(18, 2);
            e.Property(x => x.Rate).HasPrecision(18, 6);
            e.Property(x => x.BaseAmount).HasPrecision(18, 2);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<FxSettlement>(e =>
        {
            e.ToTable("fx_settlements");
            e.Property(x => x.ForeignAmount).HasPrecision(18, 2);
            e.Property(x => x.Rate).HasPrecision(18, 6);
            e.Property(x => x.BaseAmount).HasPrecision(18, 2);
            e.Property(x => x.CarryingAmount).HasPrecision(18, 2);
            e.Property(x => x.GainLoss).HasPrecision(18, 2);
            e.Property(x => x.BankCode).HasMaxLength(10).IsRequired();
            e.HasOne<FxDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
