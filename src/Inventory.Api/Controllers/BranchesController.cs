using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Branches;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

public sealed record AssignWarehouseRequest(int BranchId);
public sealed record AssignUserRequest(int? BranchId);
public sealed record BranchUserDto(int UserId, string Username, string FullName, int? BranchId);

/// <summary>Branches, which warehouses and staff belong to each, and how each is doing.</summary>
[ApiController, Route("api/branches"), RequiresNewFeatures]
[Authorize(Policy = Policies.Admin)]
public class BranchesController(ICurrentUser cu, BranchService svc, ICompanyContext company, IdentityStore store, UserManager<AppUser> users, IClock clock)
    : AppController(cu)
{
    [HttpGet]
    public Task<List<BranchDto>> List(CancellationToken ct) => svc.ListAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create(BranchInput i, CancellationToken ct) => Ok(new { id = await svc.SaveAsync(null, i, Me, ct) });

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, BranchInput i, CancellationToken ct) { await svc.SaveAsync(id, i, Me, ct); return NoContent(); }

    [HttpPut("warehouses/{warehouseId:int}")]
    public async Task<IActionResult> AssignWarehouse(int warehouseId, AssignWarehouseRequest r, CancellationToken ct) { await svc.AssignWarehouseAsync(warehouseId, r.BranchId, Me, ct); return NoContent(); }

    [HttpGet("performance")]
    public Task<List<BranchPerformance>> Performance([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        return svc.PerformanceAsync(from ?? new DateOnly(today.Year, 1, 1), to ?? today, ct);
    }

    /// <summary>Staff who can open this business, and the branch each is tied to (none = every branch).</summary>
    [HttpGet("users")]
    public async Task<List<BranchUserDto>> Users(CancellationToken ct)
    {
        var all = await users.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync(ct);
        var links = await store.UserBranches.AsNoTracking().Where(x => x.CompanyKey == company.Key).ToDictionaryAsync(x => x.UserId, x => x.BranchId, ct);
        return all.Where(u => u.CanOpen(company.Key)).Select(u => new BranchUserDto(u.Id, u.UserName!, u.FullName, links.TryGetValue(u.Id, out var b) ? b : null)).ToList();
    }

    /// <summary>Ties a user to a branch in this business (null unties them). Takes effect at their next sign-in.</summary>
    [HttpPut("users/{userId:int}")]
    public async Task<IActionResult> AssignUser(int userId, AssignUserRequest r, [FromServices] IBusinessDbContext db, CancellationToken ct)
    {
        if (await users.FindByIdAsync(userId.ToString()) is null) throw new NotFoundException("User");
        if (r.BranchId is int b && !await db.Branches.AnyAsync(x => x.Id == b && x.Active, ct)) throw new BusinessRuleException("Choose an open branch.");
        var link = await store.UserBranches.SingleOrDefaultAsync(x => x.UserId == userId && x.CompanyKey == company.Key, ct);
        if (r.BranchId is null) { if (link is not null) store.UserBranches.Remove(link); }
        else if (link is null) store.UserBranches.Add(new UserBranch { UserId = userId, CompanyKey = company.Key, BranchId = r.BranchId.Value });
        else link.BranchId = r.BranchId.Value;
        await store.SaveChangesAsync(ct);
        db.AuditLogs.Add(new Inventory.Domain.Entities.AuditLog { UserId = Me.Id, UserName = Me.FullName, Action = "USER_BRANCH_SET", Entity = "User", EntityId = userId.ToString(),
            At = clock.UtcNow, Detail = r.BranchId is int id ? $"branch {id}" : "all branches" });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
