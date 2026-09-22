using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Queries;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

[ApiController, Route("api/dashboard")]
public class DashboardController(ICurrentUser cu, DashboardQueries q) : AppController(cu)
{
    /// <summary>Profit, expenses and reorder advice are computed and returned ONLY for admins; clerks get nulls.</summary>
    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<DashboardDto> Get(CancellationToken ct) => q.GetAsync(IsAdmin, ct);
}

[ApiController, Route("api/finance")]
[Authorize(Policy = Policies.Admin)]
public class FinanceController(ICurrentUser cu, FinanceQueries q) : AppController(cu)
{
    [HttpGet("summary")]
    public Task<FinanceSummaryDto> Summary([FromQuery] int? year, [FromQuery] int? month, CancellationToken ct) => q.SummaryAsync(year, month, ct);

    [HttpGet("income")]
    public Task<List<MonthlyIncomeDto>> Income([FromQuery] int year, CancellationToken ct) => q.MonthlyIncomeAsync(year, ct);

    [HttpGet("ledger")]
    public Task<PagedResult<LedgerRowDto>> Ledger([FromQuery] PageRequest page, [FromQuery] string? accountType, CancellationToken ct) => q.LedgerAsync(page, accountType, ct);
}

public sealed record UserRow(int Id, string Username, string FullName, string Role, bool IsActive, bool MustChangePassword, string CompanyAccess, DateTime? LastLoginAt);
public sealed record CreateUserRequest(string FullName, string Username, string Password, string Role, string[] Companies);
public sealed record UpdateUserRequest(string FullName, string Role, string[] Companies, bool IsActive);
public sealed record ResetPasswordRequest(string NewPassword);

[ApiController, Route("api/users")]
[Authorize(Policy = Policies.Admin)]
public class UsersController(ICurrentUser cu, UserManager<AppUser> users, CompanyRegistry companies, ILogger<UsersController> log) : AppController(cu)
{
    private static bool ValidRole(string r) => r is RoleNames.Admin or RoleNames.Clerk;

    [HttpGet]
    public async Task<List<UserRow>> List(CancellationToken ct)
    {
        var all = await users.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync(ct);
        var rows = new List<UserRow>();
        foreach (var u in all)
            rows.Add(new UserRow(u.Id, u.UserName!, u.FullName, (await users.GetRolesAsync(u)).FirstOrDefault() ?? "", u.IsActive, u.MustChangePassword, u.CompanyAccess, u.LastLoginAt));
        return rows;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest r)
    {
        if (!ValidRole(r.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = "Role must be ADMIN or CLERK." });
        var access = r.Companies.Where(c => companies.Find(c) is not null).ToArray();
        if (access.Length == 0) return BadRequest(new ProblemDetails { Status = 400, Title = "Choose at least one company." });
        if (string.IsNullOrWhiteSpace(r.FullName) || string.IsNullOrWhiteSpace(r.Username)) return BadRequest(new ProblemDetails { Status = 400, Title = "Name and username are required." });

        var user = new AppUser { UserName = r.Username.Trim(), FullName = r.FullName.Trim(), CompanyAccess = string.Join(',', access), IsActive = true, MustChangePassword = true };
        var res = await users.CreateAsync(user, r.Password);
        if (!res.Succeeded) return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["password"] = res.Errors.Select(e => e.Description).ToArray() }));
        await users.AddToRoleAsync(user, r.Role);
        log.LogInformation("User {Username} created by {Admin}", user.UserName, Me.FullName);
        return Ok(new { id = user.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateUserRequest r)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (!ValidRole(r.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = "Role must be ADMIN or CLERK." });
        if (id == Me.Id && (!r.IsActive || r.Role != RoleNames.Admin))
            return Conflict(new ProblemDetails { Status = 409, Title = "You can't disable or demote your own account." });

        user.FullName = r.FullName.Trim();
        user.IsActive = r.IsActive;
        user.CompanyAccess = string.Join(',', r.Companies.Where(c => companies.Find(c) is not null));
        await users.UpdateAsync(user);
        var current = await users.GetRolesAsync(user);
        if (!current.Contains(r.Role)) { await users.RemoveFromRolesAsync(user, current); await users.AddToRoleAsync(user, r.Role); }
        await users.UpdateSecurityStampAsync(user);   // signs the user out everywhere
        log.LogInformation("User {UserId} updated by {Admin}", id, Me.FullName);
        return NoContent();
    }

    /// <summary>Admin sets a temporary password; the user must change it at next sign-in. Also unlocks the account.</summary>
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordRequest r)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var res = await users.ResetPasswordAsync(user, token, r.NewPassword);
        if (!res.Succeeded) return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["newPassword"] = res.Errors.Select(e => e.Description).ToArray() }));
        user.MustChangePassword = true;
        await users.UpdateAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        log.LogInformation("Password reset for user {UserId} by {Admin}", id, Me.FullName);
        return NoContent();
    }
}

[ApiController, Route("api/audit")]
[Authorize(Policy = Policies.Admin)]
public class AuditController(ICurrentUser cu, IBusinessDbContext db, IdentityStore store) : AppController(cu)
{
    [HttpGet]
    public async Task<PagedResult<AuditRowDto>> Actions([FromQuery] PageRequest page, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (page.Term is { } t) q = q.Where(a => a.Action.Contains(t) || a.UserName.Contains(t) || a.Entity.Contains(t) || (a.Detail != null && a.Detail.Contains(t)));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize)
            .Select(a => new AuditRowDto(a.Id, a.At, a.UserName, a.Action, a.Entity, a.EntityId, a.Detail)).ToListAsync(ct);
        return new PagedResult<AuditRowDto>(items, total, page.SafePage, page.SafeSize);
    }

    [HttpGet("logins")]
    public async Task<PagedResult<LoginAuditEntry>> Logins([FromQuery] PageRequest page, CancellationToken ct)
    {
        var total = await store.LoginAudit.CountAsync(ct);
        var items = await store.LoginAudit.AsNoTracking().OrderByDescending(a => a.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new PagedResult<LoginAuditEntry>(items, total, page.SafePage, page.SafeSize);
    }
}
