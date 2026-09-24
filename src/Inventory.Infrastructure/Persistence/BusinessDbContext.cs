using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>One instance talks to ONE company's schema (decision D1).</summary>
public partial class BusinessDbContext(DbContextOptions<BusinessDbContext> options) : DbContext(options), IBusinessDbContext
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockBatch> StockBatches => Set<StockBatch>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderItem> PurchaseOrderItems => Set<PurchaseOrderItem>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PriceOverride> PriceOverrides => Set<PriceOverride>();
    public DbSet<RebateEntry> RebateEntries => Set<RebateEntry>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    public DbSet<CompanyBank> CompanyBanks => Set<CompanyBank>();
    public DbSet<CompanyAsset> CompanyAssets => Set<CompanyAsset>();
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<QuotationItem> QuotationItems => Set<QuotationItem>();
    public DbSet<Waybill> Waybills => Set<Waybill>();
    public DbSet<AttendanceEvent> AttendanceEvents => Set<AttendanceEvent>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeMonthly> EmployeeMonthlies => Set<EmployeeMonthly>();
    public DbSet<EmployeeLoan> EmployeeLoans => Set<EmployeeLoan>();
    public DbSet<LoanRepayment> LoanRepayments => Set<LoanRepayment>();
    public DbSet<ProductSerial> ProductSerials => Set<ProductSerial>();
    public DbSet<PriceChange> PriceChanges => Set<PriceChange>();
    public DbSet<PaymentLink> PaymentLinks => Set<PaymentLink>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatLogMessage> ChatMessages => Set<ChatLogMessage>();

    public void ClearTracker() => ChangeTracker.Clear();

    // The MySQL provider hands back System.DateTime for DATE columns, so DateOnly needs explicit converters.
    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        cb.Properties<DateOnly>().HaveConversion<DateOnlyConverter>().HaveColumnType("date");
        cb.Properties<DateOnly?>().HaveConversion<NullableDateOnlyConverter>().HaveColumnType("date");
    }

    private sealed class DateOnlyConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateOnly, DateTime>(
        d => d.ToDateTime(TimeOnly.MinValue), d => DateOnly.FromDateTime(d));

    private sealed class NullableDateOnlyConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateOnly?, DateTime?>(
        d => d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : null, d => d.HasValue ? DateOnly.FromDateTime(d.Value) : null);

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Money is always DECIMAL, never double.
        foreach (var p in b.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            p.SetPrecision(14); // scale set per column below where different

        ConfigureMore(b);

        b.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<Warehouse>(e =>
        {
            e.ToTable("warehouses");
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Location).HasMaxLength(150);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.Property(x => x.Sku).HasMaxLength(30).IsRequired();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.Barcode).HasMaxLength(64);
            foreach (var n in new[] { nameof(Product.CostPrice), nameof(Product.SellingPrice), nameof(Product.PriceDistributor), nameof(Product.PriceWholesaler), nameof(Product.PriceRetail) })
                e.Property(n).HasPrecision(12, 2);
            e.Property(x => x.Species).HasMaxLength(20);
            e.Property(x => x.LifeStage).HasMaxLength(30);
            e.Property(x => x.NutritionSummary).HasMaxLength(300);
            foreach (var n in new[] { nameof(Product.ProteinPct), nameof(Product.FatPct), nameof(Product.FiberPct), nameof(Product.MoisturePct) })
                e.Property(n).HasPrecision(5, 2);
            e.HasIndex(x => x.Sku).IsUnique();
            e.HasIndex(x => x.Barcode).IsUnique();   // many NULLs allowed; '' is normalised to NULL by the app/migration
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockBatch>(e =>
        {
            e.ToTable("stock_batches", t => t.HasCheckConstraint("CK_stock_batches_qty_nonneg", "QuantityOnHand >= 0"));
            e.Property(x => x.BatchNumber).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.ProductId, x.WarehouseId, x.BatchNumber }).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movements", t => t.HasCheckConstraint("CK_stock_movements_type", "MovementType IN ('IN','OUT','ADJUST')"));
            e.Property(x => x.MovementType).HasMaxLength(10).IsRequired();
            e.Property(x => x.ReferenceType).HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.MovementDate).HasPrecision(6);
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => new { x.ProductId, x.MovementDate });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Supplier>(e =>
        {
            e.ToTable("suppliers");
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.ContactName).HasMaxLength(100);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(100);
            e.Property(x => x.Address).HasMaxLength(200);
            e.Property(x => x.TaxId).HasMaxLength(40);
            e.Property(x => x.Balance).HasPrecision(14, 2);
        });
        b.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("purchase_orders");
            e.Property(x => x.PoNumber).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.PaymentStatus).HasMaxLength(20).IsRequired();
            e.Property(x => x.TotalAmount).HasPrecision(14, 2);
            e.Property(x => x.AmountPaid).HasPrecision(14, 2);
            e.HasIndex(x => x.PoNumber).IsUnique();
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PurchaseOrderItem>(e =>
        {
            e.ToTable("purchase_order_items");
            e.Property(x => x.UnitCost).HasPrecision(12, 2);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.ContactName).HasMaxLength(100);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Location).HasMaxLength(150);
            e.Property(x => x.Address).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(100);
            e.Property(x => x.CustomerType).HasMaxLength(20).IsRequired();
            e.Property(x => x.TaxId).HasMaxLength(40);
            e.Property(x => x.RebateRatePct).HasPrecision(5, 2);
            e.Property(x => x.CreditLimit).HasPrecision(14, 2);
            e.Property(x => x.Balance).HasPrecision(14, 2);
        });
        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.Property(x => x.InvoiceNumber).HasMaxLength(40).IsRequired();
            e.Property(x => x.Subtotal).HasPrecision(14, 2);
            e.Property(x => x.DiscountPct).HasPrecision(5, 2);
            e.Property(x => x.DiscountAmount).HasPrecision(14, 2);
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.Property(x => x.VatAmount).HasPrecision(14, 2);
            e.Property(x => x.TotalAmount).HasPrecision(14, 2);
            e.Property(x => x.AmountPaid).HasPrecision(14, 2);
            e.Property(x => x.PaymentMethod).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.PriceTier).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.VoidReason).HasMaxLength(200);
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.InvoiceDate);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<InvoiceItem>(e =>
        {
            e.ToTable("invoice_items");
            e.Property(x => x.UnitPrice).HasPrecision(12, 2);
            e.Property(x => x.UnitCost).HasPrecision(12, 2);
            e.Property(x => x.LineTotal).HasPrecision(14, 2);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Method).HasMaxLength(20).IsRequired();
            e.Property(x => x.PaymentDate).HasPrecision(6);
        });
        b.Entity<PriceOverride>(e =>
        {
            e.ToTable("price_overrides");
            e.Property(x => x.DocType).HasMaxLength(10).IsRequired();
            e.Property(x => x.DocNumber).HasMaxLength(64).IsRequired();
            e.Property(x => x.ProductName).HasMaxLength(150).IsRequired();
            e.Property(x => x.StandardPrice).HasPrecision(12, 2);
            e.Property(x => x.OverridePrice).HasPrecision(12, 2);
            e.Property(x => x.ChangedByName).HasMaxLength(100).IsRequired();
            e.Property(x => x.ChangedAt).HasPrecision(6);
        });
        b.Entity<RebateEntry>(e =>
        {
            e.ToTable("rebate_entries");
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Status).HasMaxLength(12).IsRequired();
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger");
            e.Property(x => x.AccountType).HasMaxLength(10).IsRequired();
            e.Property(x => x.AccountName).HasMaxLength(150).IsRequired();
            e.Property(x => x.EntryType).HasMaxLength(10).IsRequired();
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Reference).HasMaxLength(64);   // widened from 30 (defect D8)
            e.HasIndex(x => x.Reference);
        });
        b.Entity<Expense>(e =>
        {
            e.ToTable("expenses");
            e.Property(x => x.Category).HasMaxLength(50).IsRequired();
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Note).HasMaxLength(200);
        });
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.UserName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Action).HasMaxLength(60).IsRequired();
            e.Property(x => x.Entity).HasMaxLength(60).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(60);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.At).HasPrecision(6);
            e.HasIndex(x => x.At);
        });
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(x => new { x.Scope, x.Key });
            e.Property(x => x.Scope).HasMaxLength(20);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasPrecision(6);
        });
    }
}
