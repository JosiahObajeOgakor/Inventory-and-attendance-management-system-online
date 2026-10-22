using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

public partial class BusinessDbContext
{
    public DbSet<ChatEvent> ChatEvents => Set<ChatEvent>();
    public DbSet<SiteReview> SiteReviews => Set<SiteReview>();
    public DbSet<SitePartner> SitePartners => Set<SitePartner>();

    private static void ConfigureChatEvents(ModelBuilder b)
    {
        b.Entity<ChatEvent>(e =>
        {
            e.ToTable("chat_events");
            e.Property(x => x.Channel).HasMaxLength(20).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.Property(x => x.Reference).HasMaxLength(40);
            e.Property(x => x.At).HasPrecision(6);
            e.HasIndex(x => new { x.At, x.Kind });
            e.HasIndex(x => x.ConversationId);
        });
        b.Entity<SiteReview>(e =>
        {
            e.ToTable("site_reviews");
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
            e.Property(x => x.Place).HasMaxLength(60);
            e.Property(x => x.Text).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.DecidedAt).HasPrecision(6);
            e.HasIndex(x => new { x.Status, x.Id });
            e.ToTable(t => t.HasCheckConstraint("CK_site_reviews_rating", "Rating BETWEEN 1 AND 5"));
        });
        b.Entity<SitePartner>(e =>
        {
            e.ToTable("site_partners");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Url).HasMaxLength(300);
            e.Property(x => x.ContentType).HasMaxLength(40).IsRequired();
            e.Property(x => x.Logo).HasColumnType("mediumblob");
            e.Property(x => x.UpdatedAt).HasPrecision(6);
        });
    }
}
