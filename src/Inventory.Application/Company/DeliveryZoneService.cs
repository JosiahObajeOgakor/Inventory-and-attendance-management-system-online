using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Company;

public sealed class DeliveryZoneInput
{
    public string Name { get; set; } = "";
    public decimal Fee { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed record DeliveryZoneDto(int Id, string Name, decimal Fee, bool IsActive);

public sealed class DeliveryZoneInputValidator : AbstractValidator<DeliveryZoneInput>
{
    public DeliveryZoneInputValidator()
    {
        RuleFor(x => x.Name).Must(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length is >= 2 and <= 60).WithMessage("Give the area a name of 2 to 60 characters.");
        RuleFor(x => x.Fee).InclusiveBetween(0, 10_000_000).WithMessage("The delivery fee must be between ₦0 and ₦10,000,000.");
    }
}

/// <summary>Where we deliver and what it costs (a state, city or area). Admins manage the list; the sales assistant offers the active ones.</summary>
public sealed class DeliveryZoneService(IBusinessDbContext db, IClock clock)
{
    public async Task<IReadOnlyList<DeliveryZoneDto>> ListAsync(bool activeOnly, CancellationToken ct = default) =>
        await db.DeliveryZones.AsNoTracking().Where(z => !activeOnly || z.IsActive).OrderBy(z => z.Name)
            .Select(z => new DeliveryZoneDto(z.Id, z.Name, z.Fee, z.IsActive)).ToListAsync(ct);

    /// <summary>An active zone by name, ignoring case and surrounding spaces. Null when there's no such active zone.</summary>
    public async Task<DeliveryZoneDto?> FindActiveAsync(string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().ToLower();
        return await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive && z.Name.ToLower() == n)
            .Select(z => new DeliveryZoneDto(z.Id, z.Name, z.Fee, z.IsActive)).FirstOrDefaultAsync(ct);
    }

    public async Task<int> CreateAsync(DeliveryZoneInput input, CurrentUser user, CancellationToken ct = default)
    {
        await new DeliveryZoneInputValidator().ValidateAndThrowAsync(input, ct);
        var name = input.Name.Trim();
        await EnsureUniqueAsync(name, null, ct);
        var z = new DeliveryZone { Name = name, Fee = input.Fee, IsActive = input.IsActive };
        db.DeliveryZones.Add(z);
        Audit(user, "DELIVERY_ZONE_ADDED", z, $"{name}: ₦{input.Fee:N2}");
        await db.SaveChangesAsync(ct);
        return z.Id;
    }

    public async Task UpdateAsync(int id, DeliveryZoneInput input, CurrentUser user, CancellationToken ct = default)
    {
        await new DeliveryZoneInputValidator().ValidateAndThrowAsync(input, ct);
        var z = await db.DeliveryZones.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Delivery zone");
        var name = input.Name.Trim();
        await EnsureUniqueAsync(name, id, ct);
        var before = $"{z.Name}: ₦{z.Fee:N2}{(z.IsActive ? "" : " (off)")}";
        z.Name = name; z.Fee = input.Fee; z.IsActive = input.IsActive;
        Audit(user, "DELIVERY_ZONE_CHANGED", z, $"{before} → {name}: ₦{input.Fee:N2}{(input.IsActive ? "" : " (off)")}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Past orders keep the zone name and fee they were charged (stored on the order), so deleting a zone never changes history.</summary>
    public async Task DeleteAsync(int id, CurrentUser user, CancellationToken ct = default)
    {
        var z = await db.DeliveryZones.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Delivery zone");
        db.DeliveryZones.Remove(z);
        Audit(user, "DELIVERY_ZONE_REMOVED", z, z.Name);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureUniqueAsync(string name, int? exceptId, CancellationToken ct)
    {
        var n = name.ToLower();
        if (await db.DeliveryZones.AnyAsync(z => z.Name.ToLower() == n && z.Id != (exceptId ?? 0), ct))
            throw new BusinessRuleException($"There is already a delivery zone called {name}.");
    }

    private void Audit(CurrentUser user, string action, DeliveryZone z, string detail) =>
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = action, Entity = "DeliveryZone", EntityId = z.Id == 0 ? null : z.Id.ToString(), At = clock.UtcNow, Detail = detail });
}
