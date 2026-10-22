using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>Tables for the record-keeping apps (Sign, Events, Fleet…) and for Time Off.</summary>
public partial class BusinessDbContext
{
    public DbSet<AppRecord> AppRecords => Set<AppRecord>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

    private static void ConfigureApps(ModelBuilder b)
    {
        b.Entity<AppRecord>(e =>
        {
            e.ToTable("app_records");
            e.Property(x => x.App).HasMaxLength(30).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Code).HasMaxLength(80);
            e.Property(x => x.Amount).HasPrecision(16, 2);
            e.Property(x => x.Data).HasColumnType("longtext").IsRequired();
            e.Property(x => x.CreatedByName).HasMaxLength(120);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.UpdatedAt).HasPrecision(6);
            e.HasIndex(x => new { x.App, x.Kind, x.Status });
            e.HasIndex(x => new { x.App, x.Kind, x.ParentId });
            e.HasIndex(x => new { x.App, x.Kind, x.Code }).IsUnique();   // several NULL codes are fine; a real code is unique
            e.HasOne<AppRecord>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CampaignRecipient>(e =>
        {
            e.ToTable("campaign_recipients");
            e.Property(x => x.Channel).HasMaxLength(10).IsRequired();
            e.Property(x => x.Address).HasMaxLength(150).IsRequired();
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.Error).HasMaxLength(300);
            e.Property(x => x.DedupeKey).HasMaxLength(80);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.SentAt).HasPrecision(6);
            e.HasIndex(x => new { x.RecordId, x.Status });
            e.HasIndex(x => new { x.Status, x.Id });
            e.HasIndex(x => new { x.RecordId, x.DedupeKey }).IsUnique();
            e.HasOne<AppRecord>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<LeaveType>(e =>
        {
            e.ToTable("leave_types", t => t.HasCheckConstraint("CK_leave_types_days", "DaysPerYear >= 0"));
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<LeaveRequest>(e =>
        {
            e.ToTable("leave_requests", t => t.HasCheckConstraint("CK_leave_requests_dates", "ToDate >= FromDate AND Days > 0"));
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.RequestedByName).HasMaxLength(120);
            e.Property(x => x.DecidedByName).HasMaxLength(120);
            e.Property(x => x.DecisionNote).HasMaxLength(300);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            e.Property(x => x.DecidedAt).HasPrecision(6);
            e.HasIndex(x => new { x.EmployeeId, x.FromDate });
            e.HasIndex(x => x.Status);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeaveType>().WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
