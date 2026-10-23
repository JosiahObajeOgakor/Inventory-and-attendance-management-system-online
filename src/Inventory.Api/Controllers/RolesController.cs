using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

public sealed record RoleRow(string Name, bool BuiltIn, int Users, IReadOnlyList<string> Permissions);
public sealed record SetRolePermissionsRequest(string[] Permissions);
public sealed record CreateRoleRequest(string Name, string? CopyFrom);

/// <summary>
/// Roles are bundles of permissions the CEO ticks on and off (People &amp; access → Roles). The four built-in roles start with what they could always do;
/// new roles can be made from scratch or copied. A change reaches everyone in the role within a minute (their session refreshes its permissions).
/// </summary>
[ApiController, Route("api/roles"), Authorize(Policy = Perm.UsersManage)]
public class RolesController(ICurrentUser cu, RoleManager<IdentityRole<int>> roles, UserManager<AppUser> users, IdentityStore store, IBusinessDbContext db, IClock clock,
    ILogger<RolesController> log) : AppController(cu)
{
    private static readonly string[] BuiltIn = [.. RoleNames.All];

    [HttpGet("permissions")]
    public IReadOnlyList<string> AllPermissions() => Permissions.All;

    [HttpGet]
    public async Task<List<RoleRow>> List(CancellationToken ct)
    {
        var all = await roles.Roles.AsNoTracking().Where(r => r.Name != RoleNames.LegacyAdmin && r.Name != RoleNames.LegacyClerk).OrderBy(r => r.Name).ToListAsync(ct);
        var grants = await store.RolePermissions.AsNoTracking().ToListAsync(ct);
        var counts = await store.UserRoles.AsNoTracking().GroupBy(ur => ur.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return all.Select(r => new RoleRow(r.Name!, BuiltIn.Contains(r.Name), counts.GetValueOrDefault(r.Id),
            grants.Where(g => g.RoleId == r.Id).Select(g => g.Permission).OrderBy(p => p).ToList())).ToList();
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateRoleRequest r, CancellationToken ct)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 60) return BadRequest(new ProblemDetails { Status = 400, Title = "Give the role a name (2 to 60 characters)." });
        if (await roles.RoleExistsAsync(name)) return Conflict(new ProblemDetails { Status = 409, Title = "A role with that name already exists." });
        await roles.CreateAsync(new IdentityRole<int>(name));
        var role = (await roles.FindByNameAsync(name))!;
        if (!string.IsNullOrWhiteSpace(r.CopyFrom) && await roles.FindByNameAsync(r.CopyFrom) is { } source)
        {
            var copy = await store.RolePermissions.AsNoTracking().Where(p => p.RoleId == source.Id).Select(p => p.Permission).ToListAsync(ct);
            store.RolePermissions.AddRange(copy.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
            await store.SaveChangesAsync(ct);
        }
        log.LogInformation("Role {Role} created by {Admin}", name, Me.FullName);
        return Ok(new { name });
    }

    /// <summary>Replaces what a role grants. Unknown codes are refused; ADMIN always keeps every permission so the business can't lock itself out.</summary>
    [HttpPut("{name}/permissions")]
    public async Task<IActionResult> SetPermissions(string name, SetRolePermissionsRequest r, CancellationToken ct)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null) return NotFound();
        if (name == RoleNames.Admin) return Conflict(new ProblemDetails { Status = 409, Title = "The CEO role always has every permission." });
        var wanted = (r.Permissions ?? []).Distinct().ToList();
        var unknown = wanted.Where(p => !Permissions.IsKnown(p)).ToList();
        if (unknown.Count > 0) return BadRequest(new ProblemDetails { Status = 400, Title = "Unknown permission(s): " + string.Join(", ", unknown) });
        if ((await users.GetUsersInRoleAsync(name)).Any(u => u.Id == Me.Id) && !wanted.Contains(Permissions.UsersManage))
            return Conflict(new ProblemDetails { Status = 409, Title = "That would remove your own access to People & access." });

        store.RolePermissions.RemoveRange(store.RolePermissions.Where(p => p.RoleId == role.Id));
        store.RolePermissions.AddRange(wanted.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        await store.SaveChangesAsync(ct);
        db.AuditLogs.Add(new Inventory.Domain.Entities.AuditLog { UserId = Me.Id, UserName = Me.FullName, Action = "ROLE_PERMISSIONS_SET", Entity = "Role", EntityId = name, At = clock.UtcNow,
            Detail = $"{name}: {wanted.Count} permission(s)" });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Role {Role} permissions set to {Count} by {Admin}", name, wanted.Count, Me.FullName);
        return NoContent();
    }

    /// <summary>A role nobody holds can be removed (the four built-in roles stay).</summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (BuiltIn.Contains(name)) return Conflict(new ProblemDetails { Status = 409, Title = "The built-in roles can't be deleted." });
        var role = await roles.FindByNameAsync(name);
        if (role is null) return NotFound();
        if ((await users.GetUsersInRoleAsync(name)).Count > 0) return Conflict(new ProblemDetails { Status = 409, Title = "Move the people in this role to another role first." });
        await roles.DeleteAsync(role);
        log.LogInformation("Role {Role} deleted by {Admin}", name, Me.FullName);
        return NoContent();
    }
}
