using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>Branches, and the branch each warehouse belongs to.</summary>
public partial class BusinessDbContext
{
    public DbSet<Branch> Branches => Set<Branch>();

    private static void ConfigureBranches(ModelBuilder b)
    {
        b.Entity<Branch>(e =>
        {
            e.ToTable("branches");
            e.Property(x => x.Code).HasMaxLength(10).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Address).HasMaxLength(250);
            e.Property(x => x.State).HasMaxLength(60);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => x.Code).IsUnique();
        });
        b.Entity<Warehouse>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}
