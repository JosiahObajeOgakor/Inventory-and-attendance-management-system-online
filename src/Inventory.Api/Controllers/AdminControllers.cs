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
    [HttpGet, Authorize(Policy = Perm.ProductsView)]
    public Task<DashboardDto> Get(CancellationToken ct) => q.GetAsync(IsAdmin, ct);
}

[ApiController, Route("api/finance")]
[Authorize(Policy = Perm.FinanceView)]
public class FinanceController(ICurrentUser cu, FinanceQueries q) : AppController(cu)
{
    [HttpGet("summary")]
    public Task<FinanceSummaryDto> Summary([FromQuery] int? year, [FromQuery] int? month, CancellationToken ct) => q.SummaryAsync(year, month, ct);

    [HttpGet("income")]
    public Task<List<MonthlyIncomeDto>> Income([FromQuery] int year, CancellationToken ct) => q.MonthlyIncomeAsync(year, ct);

    [HttpGet("ledger")]
    public Task<PagedResult<LedgerRowDto>> Ledger([FromQuery] PageRequest paging, [FromQuery] string? accountType, CancellationToken ct) => q.LedgerAsync(paging, accountType, ct);
}

public sealed record UserRow(int Id, string Username, string FullName, string Role, bool IsActive, bool MustChangePassword, string CompanyAccess, DateTime? LastLoginAt, string? Email = null);
public sealed record CreateUserRequest(string FullName, string Username, string Password, string Role, string[] Companies, string? Email = null);
public sealed record UpdateUserRequest(string FullName, string Role, string[] Companies, bool IsActive, string? Email = null);
public sealed record ResetPasswordRequest(string NewPassword);

[ApiController, Route("api/users")]
[Authorize(Policy = Perm.UsersManage)]
public class UsersController(ICurrentUser cu, UserManager<AppUser> users, CompanyRegistry companies, RoleManager<IdentityRole<int>> roles, IdentityStore store,
    ILogger<UsersController> log) : AppController(cu)
{
    private Task<bool> ValidRole(string r) => string.IsNullOrWhiteSpace(r) ? Task.FromResult(false) : roles.RoleExistsAsync(r);
    private const string BadRole = "Choose one of the roles listed under People & access → Roles.";

    private async Task<bool> RoleGrants(string role, string permission)
    {
        var id = (await roles.FindByNameAsync(role))?.Id;
        return id is int rid && await store.RolePermissions.AnyAsync(p => p.RoleId == rid && p.Permission == permission);
    }

    /// <summary>null when blank (no email), "" when it is not an email address, else the trimmed address. Clerks get the sale emails here.</summary>
    private static string? CleanEmail(string? e)
    {
        if (string.IsNullOrWhiteSpace(e)) return null;
        var s = e.Trim();
        return s.Length <= 120 && s.Contains('@') && s.Contains('.') && !s.Any(char.IsWhiteSpace) ? s : "";
    }

    [HttpGet]
    public async Task<List<UserRow>> List(CancellationToken ct)
    {
        var all = await users.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync(ct);
        var rows = new List<UserRow>();
        foreach (var u in all)
            rows.Add(new UserRow(u.Id, u.UserName!, u.FullName, (await users.GetRolesAsync(u)).FirstOrDefault() ?? "", u.IsActive, u.MustChangePassword, u.CompanyAccess, u.LastLoginAt, u.Email));
        return rows;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest r)
    {
        if (!await ValidRole(r.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = BadRole });
        var access = r.Companies.Where(c => companies.Find(c) is not null).ToArray();
        if (access.Length == 0) return BadRequest(new ProblemDetails { Status = 400, Title = "Choose at least one company." });
        if (string.IsNullOrWhiteSpace(r.FullName) || string.IsNullOrWhiteSpace(r.Username)) return BadRequest(new ProblemDetails { Status = 400, Title = "Name and username are required." });

        var mail = CleanEmail(r.Email);
        if (mail == "") return BadRequest(new ProblemDetails { Status = 400, Title = "That doesn't look like a valid email address." });
        var user = new AppUser { UserName = r.Username.Trim(), FullName = r.FullName.Trim(), CompanyAccess = string.Join(',', access), IsActive = true, MustChangePassword = true, Email = mail };
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
        if (!await ValidRole(r.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = BadRole });
        if (id == Me.Id && (!r.IsActive || !await RoleGrants(r.Role, Permissions.UsersManage)))
            return Conflict(new ProblemDetails { Status = 409, Title = "You can't disable your own account or move yourself to a role that can't manage people." });

        var mail = CleanEmail(r.Email);
        if (mail == "") return BadRequest(new ProblemDetails { Status = 400, Title = "That doesn't look like a valid email address." });
        user.FullName = r.FullName.Trim();
        user.Email = mail;
        user.IsActive = r.IsActive;
        user.CompanyAccess = string.Join(',', r.Companies.Where(c => companies.Find(c) is not null));
        await users.UpdateAsync(user);
        var current = await users.GetRolesAsync(user);
        if (!current.Contains(r.Role)) { await users.RemoveFromRolesAsync(user, current); await users.AddToRoleAsync(user, r.Role); }
        await users.UpdateSecurityStampAsync(user);   // signs the user out everywhere, so a new or revoked role applies at once
        log.LogInformation("User {UserId} updated by {Admin} (role {Role})", id, Me.FullName, r.Role);
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
[Authorize(Policy = Perm.AuditView)]
public class AuditController(ICurrentUser cu, IBusinessDbContext db, IdentityStore store) : AppController(cu)
{
    [HttpGet]
    public async Task<PagedResult<AuditRowDto>> Actions([FromQuery] PageRequest paging, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        foreach (var w in Inventory.Application.Queries.SearchTerms.Words(paging.Term))
            q = q.Where(a => a.Action.Contains(w) || a.UserName.Contains(w) || a.Entity.Contains(w) || (a.EntityId != null && a.EntityId.Contains(w)) || (a.Detail != null && a.Detail.Contains(w)));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Id).Skip((paging.SafePage - 1) * paging.SafeSize).Take(paging.SafeSize)
            .Select(a => new AuditRowDto(a.Id, a.At, a.UserName, a.Action, a.Entity, a.EntityId, a.Detail)).ToListAsync(ct);
        return new PagedResult<AuditRowDto>(items, total, paging.SafePage, paging.SafeSize);
    }

    [HttpGet("logins")]
    public async Task<PagedResult<LoginAuditEntry>> Logins([FromQuery] PageRequest paging, CancellationToken ct)
    {
        var total = await store.LoginAudit.CountAsync(ct);
        var items = await store.LoginAudit.AsNoTracking().OrderByDescending(a => a.Id).Skip((paging.SafePage - 1) * paging.SafeSize).Take(paging.SafeSize).ToListAsync(ct);
        return new PagedResult<LoginAuditEntry>(items, total, paging.SafePage, paging.SafeSize);
    }
}
