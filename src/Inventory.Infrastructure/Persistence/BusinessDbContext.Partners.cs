using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

public partial class BusinessDbContext
{
    public DbSet<TradePartner> TradePartners => Set<TradePartner>();
    public DbSet<TradePartnerDocument> TradePartnerDocuments => Set<TradePartnerDocument>();
    public DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();
    public DbSet<TradeApplicationDraft> TradeApplicationDrafts => Set<TradeApplicationDraft>();
    public DbSet<TradeApplicationFile> TradeApplicationFiles => Set<TradeApplicationFile>();
    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();
    public DbSet<PartnerMessage> PartnerMessages => Set<PartnerMessage>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();

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
        b.Entity<TradeApplicationDraft>(e =>
        {
            e.ToTable("trade_application_drafts");
            e.Property(x => x.Email).HasMaxLength(150).IsRequired();
            e.Property(x => x.DeviceTokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.DataJson).HasColumnType("text").IsRequired();
            e.Property(x => x.EmailVerifiedAt).HasPrecision(6);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.LastActivityAt).HasPrecision(6);
            e.Property(x => x.LastReminderAt).HasPrecision(6);
            e.Property(x => x.WarnedAt).HasPrecision(6);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.DeviceTokenHash);
            e.HasIndex(x => x.LastActivityAt);
            e.HasMany(x => x.Files).WithOne().HasForeignKey(f => f.DraftId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TradeApplicationFile>(e =>
        {
            e.ToTable("trade_application_files");
            e.Property(x => x.Kind).HasMaxLength(10).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(150).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(60).IsRequired();
            e.Property(x => x.Data).HasColumnType("mediumblob");
            e.Property(x => x.UploadedAt).HasPrecision(6);
        });
        b.Entity<EmailCode>(e =>
        {
            e.ToTable("email_codes");
            e.Property(x => x.Email).HasMaxLength(150).IsRequired();
            e.Property(x => x.Purpose).HasMaxLength(10).IsRequired();
            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.ExpiresAt).HasPrecision(6);
            e.Property(x => x.UsedAt).HasPrecision(6);
            e.HasIndex(x => new { x.Email, x.Purpose, x.CreatedAt });
        });
        b.Entity<PartnerMessage>(e =>
        {
            e.ToTable("partner_messages");
            e.Property(x => x.Text).HasMaxLength(2000).IsRequired();
            e.Property(x => x.By).HasMaxLength(100);
            e.Property(x => x.At).HasPrecision(6);
            e.Property(x => x.ReadAt).HasPrecision(6);
            e.HasIndex(x => new { x.TradePartnerId, x.Id });
            e.HasOne<TradePartner>().WithMany().HasForeignKey(x => x.TradePartnerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TrustedDevice>(e =>
        {
            e.ToTable("trusted_devices");
            e.Property(x => x.Email).HasMaxLength(150).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.FirstIp).HasMaxLength(45);
            e.Property(x => x.LastIp).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.LastUsedAt).HasPrecision(6);
            e.Property(x => x.ExpiresAt).HasPrecision(6);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.Email);
        });
    }
}
