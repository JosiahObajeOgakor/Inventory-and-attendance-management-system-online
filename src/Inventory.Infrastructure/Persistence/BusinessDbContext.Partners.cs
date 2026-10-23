using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

public partial class BusinessDbContext
{
    public DbSet<TradePartner> TradePartners => Set<TradePartner>();
    public DbSet<TradePartnerDocument> TradePartnerDocuments => Set<TradePartnerDocument>();
    public DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();

    private static void ConfigurePartners(ModelBuilder b)
    {
        b.Entity<TradePartner>(e =>
        {
            e.ToTable("trade_partners");
            e.Property(x => x.BusinessName).HasMaxLength(150).IsRequired();
            e.Property(x => x.ContactName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            e.Property(x => x.Email).HasMaxLength(150).IsRequired();
            e.Property(x => x.Address).HasMaxLength(300).IsRequired();
            e.Property(x => x.CacNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.TaxId).HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            e.Property(x => x.RequestedTier).HasMaxLength(20).IsRequired();
            e.Property(x => x.ApprovedTier).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(12).IsRequired();
            e.Property(x => x.Channel).HasMaxLength(12).IsRequired();
            e.Property(x => x.Verification).HasMaxLength(12).IsRequired();
            e.Property(x => x.VerifiedName).HasMaxLength(150);
            e.Property(x => x.VerificationNote).HasMaxLength(300);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.DecidedBy).HasMaxLength(100);
            e.Property(x => x.DecisionNote).HasMaxLength(300);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.DecidedAt).HasPrecision(6);
            // one application per phone number: re-applying updates it instead of piling up duplicates
            e.HasIndex(x => x.Phone).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Documents).WithOne().HasForeignKey(d => d.TradePartnerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TradePartnerDocument>(e =>
        {
            e.ToTable("trade_partner_documents");
            e.Property(x => x.Kind).HasMaxLength(10).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(150).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(60).IsRequired();
            e.Property(x => x.Data).HasColumnType("mediumblob");
            e.Property(x => x.UploadedAt).HasPrecision(6);
        });
        b.Entity<WalletEntry>(e =>
        {
            e.ToTable("wallet_entries");
            e.Property(x => x.Kind).HasMaxLength(12).IsRequired();
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(60).IsRequired();
            e.Property(x => x.Method).HasMaxLength(12).IsRequired();
            e.Property(x => x.Note).HasMaxLength(300);
            e.Property(x => x.ConfirmedBy).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.ConfirmedAt).HasPrecision(6);
            // the idempotency key: the same money can be recorded only once
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.TradePartnerId, x.Status });
            e.HasOne<TradePartner>().WithMany().HasForeignKey(x => x.TradePartnerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
