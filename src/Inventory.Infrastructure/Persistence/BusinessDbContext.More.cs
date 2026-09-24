using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

public partial class BusinessDbContext
{
    /// <summary>Model configuration for company profile, quotations, waybills, attendance, payroll, serials and price history.</summary>
    private static void ConfigureMore(ModelBuilder b)
    {
        b.Entity<CompanyProfile>(e =>
        {
            e.ToTable("company_profile");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LegalName).HasMaxLength(150).IsRequired();
            e.Property(x => x.Address).HasMaxLength(250).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(60).IsRequired();
            e.Property(x => x.Email).HasMaxLength(100).IsRequired();
            e.Property(x => x.TaxId).HasMaxLength(60).IsRequired();
            e.Property(x => x.DefaultVatRate).HasPrecision(5, 2);
            e.Property(x => x.DefaultRebateRatePct).HasPrecision(5, 2);
            e.HasMany(x => x.Banks).WithOne().HasForeignKey(x => x.CompanyProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CompanyBank>(e =>
        {
            e.ToTable("company_banks");
            e.Property(x => x.BankName).HasMaxLength(80).IsRequired();
            e.Property(x => x.AccountName).HasMaxLength(150).IsRequired();
            e.Property(x => x.AccountNumber).HasMaxLength(40).IsRequired();
        });
        b.Entity<CompanyAsset>(e =>
        {
            e.ToTable("company_assets");
            e.HasKey(x => x.Kind);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.ContentType).HasMaxLength(60).IsRequired();
            e.Property(x => x.Data).HasColumnType("longblob");
            e.Property(x => x.UpdatedAt).HasPrecision(6);
        });

        b.Entity<Quotation>(e =>
        {
            e.ToTable("quotations");
            e.Property(x => x.QuotationNumber).HasMaxLength(40).IsRequired();
            e.Property(x => x.Subtotal).HasPrecision(14, 2);
            e.Property(x => x.DiscountPct).HasPrecision(5, 2);
            e.Property(x => x.DiscountAmount).HasPrecision(14, 2);
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.Property(x => x.VatAmount).HasPrecision(14, 2);
            e.Property(x => x.TotalAmount).HasPrecision(14, 2);
            e.Property(x => x.PriceTier).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.HasIndex(x => x.QuotationNumber).IsUnique();
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.ConvertedInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<QuotationItem>(e =>
        {
            e.ToTable("quotation_items");
            e.Property(x => x.UnitPrice).HasPrecision(12, 2);
            e.Property(x => x.LineTotal).HasPrecision(14, 2);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Waybill>(e =>
        {
            e.ToTable("waybills");
            e.Property(x => x.WaybillNumber).HasMaxLength(40).IsRequired();
            e.Property(x => x.DriverName).HasMaxLength(120);
            e.Property(x => x.DriverPhone).HasMaxLength(30);
            e.Property(x => x.VehiclePlate).HasMaxLength(20);
            e.Property(x => x.DestinationAddress).HasMaxLength(250);
            e.Property(x => x.Notes).HasMaxLength(250);
            e.HasIndex(x => x.WaybillNumber).IsUnique();
            e.HasIndex(x => x.InvoiceId);
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AttendanceEvent>(e =>
        {
            e.ToTable("attendance_events", t => t.HasCheckConstraint("CK_attendance_type", "EventType IN ('In','Out','Declined')"));
            e.Property(x => x.FullName).HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(10).IsRequired();
            e.Property(x => x.HappenedAt).HasPrecision(6);
            e.HasIndex(x => new { x.UserId, x.WorkDate });
            e.HasIndex(x => x.WorkDate);
        });

        b.Entity<Employee>(e =>
        {
            e.ToTable("employees");
            e.Property(x => x.FullName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Position).HasMaxLength(80).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.MonthlySalary).HasPrecision(14, 2);
        });
        b.Entity<EmployeeMonthly>(e =>
        {
            e.ToTable("employee_monthly");
            e.Property(x => x.Position).HasMaxLength(80).IsRequired();
            e.Property(x => x.SalaryAmount).HasPrecision(14, 2);
            e.Property(x => x.LoanDeduction).HasPrecision(14, 2);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => new { x.EmployeeId, x.PeriodYear, x.PeriodMonth }).IsUnique();
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<EmployeeLoan>(e =>
        {
            e.ToTable("employee_loans");
            e.Property(x => x.Principal).HasPrecision(14, 2);
            e.Property(x => x.Balance).HasPrecision(14, 2);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LoanRepayment>(e =>
        {
            e.ToTable("loan_repayments");
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne<EmployeeLoan>().WithMany().HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductSerial>(e =>
        {
            e.ToTable("product_serials", t => t.HasCheckConstraint("CK_serial_status", "Status IN ('In Stock','Sold','Returned','Written Off')"));
            e.Property(x => x.SerialNumber).HasMaxLength(80).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(200);
            e.Property(x => x.ReceivedAt).HasPrecision(6);
            e.Property(x => x.SoldAt).HasPrecision(6);
            e.HasIndex(x => new { x.ProductId, x.SerialNumber }).IsUnique();
            e.HasIndex(x => new { x.ProductId, x.Status });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PaymentLink>(e =>
        {
            e.Property(x => x.Reference).HasMaxLength(80).IsRequired();
            e.HasIndex(x => x.Reference).IsUnique();
            e.Property(x => x.DocType).HasMaxLength(20).IsRequired();
            e.Property(x => x.DocNumber).HasMaxLength(60);
            e.Property(x => x.CustomerName).HasMaxLength(150);
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.PaidAmount).HasPrecision(14, 2);
            e.Property(x => x.Url).HasMaxLength(400).IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.Channel).HasMaxLength(40);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.PaidAt).HasPrecision(6);
            e.Property(x => x.NotifiedAt).HasPrecision(6);
            e.HasIndex(x => new { x.DocType, x.DocId, x.Status });
            e.ToTable("payment_links", t => t.HasCheckConstraint("CK_payment_links_status", "Status IN ('Pending','Paid')"));
        });


        b.Entity<PriceChange>(e =>
        {
            e.ToTable("price_changes");
            foreach (var n in new[] { nameof(PriceChange.OldDistributor), nameof(PriceChange.OldWholesaler), nameof(PriceChange.OldRetail),
                                      nameof(PriceChange.NewDistributor), nameof(PriceChange.NewWholesaler), nameof(PriceChange.NewRetail) })
                e.Property(n).HasPrecision(12, 2);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.ChangedAt).HasPrecision(6);
            e.HasIndex(x => new { x.ProductId, x.ChangedAt });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChatConversation>(e =>
        {
            e.ToTable("chat_conversations");
            e.Property(x => x.Channel).HasMaxLength(10).IsRequired();
            e.Property(x => x.ExternalId).HasMaxLength(100).IsRequired();
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.LastMessageAt).HasPrecision(6);
            e.HasIndex(x => new { x.Channel, x.ExternalId }).IsUnique();
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Quotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ChatLogMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.Property(x => x.Role).HasMaxLength(10).IsRequired();
            e.Property(x => x.Text).HasMaxLength(2000).IsRequired();
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
