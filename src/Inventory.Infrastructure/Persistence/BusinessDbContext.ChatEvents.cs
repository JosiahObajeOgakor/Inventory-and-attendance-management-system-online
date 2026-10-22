using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

public partial class BusinessDbContext
{
    public DbSet<ChatEvent> ChatEvents => Set<ChatEvent>();

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
    }
}
