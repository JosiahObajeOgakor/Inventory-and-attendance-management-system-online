using System.Security.Claims;
using Inventory.Api.Infrastructure;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

public sealed record LoginRequest(string Username, string Password, string Company);
public sealed record CompanyChoice(string Key, string DisplayName, bool HasPriceLists = false, bool BuysGoods = false);
public sealed record MeResponse(int Id, string Username, string FullName, string Role, string Company, IReadOnlyList<CompanyChoice> Companies, bool MustChangePassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SwitchCompanyRequest(string Company);

[ApiController]
[Route("api/auth")]
public class AuthController(
    UserManager<AppUser> users, SignInManager<AppUser> signIn, CompanyRegistry companies,
    IdentityStore store, ILogger<AuthController> log) : ControllerBase
{
    // One message for every credential failure: no user enumeration (the desktop app said "Unknown username").
    private const string BadCredentials = "Incorrect username or password.";

    /// <summary>The businesses shown on the sign-in screen (names only; nothing sensitive).</summary>
    [HttpGet("companies")]
    [AllowAnonymous]
    public IEnumerable<CompanyChoice> Companies() => companies.All.Select(c => new CompanyChoice(c.Key, c.DisplayName, c.HasPriceLists, c.BuysGoods));

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<ActionResult<MeResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var username = (req.Username ?? "").Trim();
        var company = companies.Find(req.Company);
        var user = username.Length == 0 ? null : await users.FindByNameAsync(username);

        if (user is null || company is null)
        {
            await Audit(username, false, user is null ? "unknown user" : "unknown company", ct);
            return Unauthorized(Problem401(BadCredentials));
        }
        if (!user.IsActive) { await Audit(username, false, "disabled", ct); return Unauthorized(Problem401(BadCredentials)); }
        if (await users.IsLockedOutAsync(user)) { await Audit(username, false, "locked", ct); return StatusCode(423, Problem401("Too many wrong passwords. Try again in a few minutes.")); }

        var result = await signIn.CheckPasswordSignInAsync(user, req.Password ?? "", lockoutOnFailure: true);
        if (result.IsLockedOut) { await Audit(username, false, "locked after failures", ct); return StatusCode(423, Problem401("Too many wrong passwords. Try again in a few minutes.")); }
        if (!result.Succeeded) { await Audit(username, false, "bad password", ct); return Unauthorized(Problem401(BadCredentials)); }
        if (!user.CanOpen(company.Key)) { await Audit(username, false, "no access to company", ct); return Unauthorized(Problem401(BadCredentials)); }

        user.LastLoginAt = DateTime.UtcNow;
        await users.UpdateAsync(user);
        await Audit(username, true, null, ct);
        await SignInAsync(user, company.Key);
        return Ok(await BuildMe(user, company.Key));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        // A clerk signing out is checking out for the day; the log never blocks the sign-out.
        try
        {
            var me = HttpContext.RequestServices.GetRequiredService<Inventory.Application.Abstractions.ICurrentUser>().User;
            if (me is not null && me.Role is not RoleNames.Admin)
                await HttpContext.RequestServices.GetRequiredService<Inventory.Application.Staff.AttendanceService>().CheckOutAsync(me, ct);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not log check-out"); }
        await signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> Me()
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) { await signIn.SignOutAsync(); return Unauthorized(); }
        return Ok(await BuildMe(user, User.FindFirstValue(AppClaims.Company)!));
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        var result = await users.ChangePasswordAsync(user, req.CurrentPassword ?? "", req.NewPassword ?? "");
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["newPassword"] = result.Errors.Select(e => e.Description).ToArray() }));
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await SignInAsync(user, User.FindFirstValue(AppClaims.Company)!);   // refreshes claims (clears must-change)
        return NoContent();
    }

    [HttpPost("company")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> SwitchCompany(SwitchCompanyRequest req)
    {
        var user = await users.GetUserAsync(User);
        var company = companies.Find(req.Company);
        if (user is null || company is null || !user.CanOpen(company.Key)) return Forbid();
        await SignInAsync(user, company.Key);
        return Ok(await BuildMe(user, company.Key));
    }

    private async Task SignInAsync(AppUser user, string companyKey)
    {
        var extra = new List<Claim> { new(AppClaims.Company, companyKey), new(AppClaims.FullName, user.FullName) };
        if (user.MustChangePassword) extra.Add(new Claim(MustChangePasswordMiddleware.ClaimType, "true"));
        await signIn.SignOutAsync();
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, extra);
    }

    private async Task<MeResponse> BuildMe(AppUser user, string companyKey)
    {
        var role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? "";
        var choices = companies.All.Where(c => user.CanOpen(c.Key)).Select(c => new CompanyChoice(c.Key, c.DisplayName, c.HasPriceLists, c.BuysGoods)).ToList();
        return new MeResponse(user.Id, user.UserName!, user.FullName, role, companyKey, choices, user.MustChangePassword);
    }

    private async Task Audit(string username, bool ok, string? reason, CancellationToken ct)
    {
        try
        {
            store.LoginAudit.Add(new LoginAuditEntry
            {
                Username = username.Length > 50 ? username[..50] : username, Succeeded = ok, Reason = reason,
                ClientAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), AtUtc = DateTime.UtcNow,
            });
            await store.SaveChangesAsync(ct);
            if (!ok) log.LogWarning("Failed login for {Username}: {Reason}", username, reason);   // never the password
        }
        catch (Exception ex) { log.LogError(ex, "Could not write login audit"); }
    }

    private static ProblemDetails Problem401(string title) => new() { Status = 401, Title = title };
}

[ApiController]
[Route("api/health")]
public class HealthController(IdentityStore store) : ControllerBase
{
    /// <summary>Liveness + database reachability. Deliberately reveals nothing else about the system.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        try
        {
            await store.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return Ok(new { status = "healthy" });
        }
        catch { return StatusCode(503, new { status = "unhealthy" }); }
    }
}

public sealed record FirstAdminRequest(string SetupToken, string FullName, string Username, string Password);

[ApiController]
[Route("api/setup")]
public class SetupController(UserManager<AppUser> users, CompanyRegistry companies, IConfiguration config) : ControllerBase
{
    /// <summary>
    /// Creates the first administrator on a brand-new installation. Disabled unless Setup:Token is configured, and it stops
    /// working the moment any admin exists — so it cannot be used to take over a live system. (The desktop app let anyone
    /// create the first admin from the login screen; that is not safe on a public server.)
    /// </summary>
    [HttpPost("first-admin")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<IActionResult> FirstAdmin(FirstAdminRequest req)
    {
        var token = config["Setup:Token"];
        if (string.IsNullOrEmpty(token)) return NotFound();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(req.SetupToken ?? ""), System.Text.Encoding.UTF8.GetBytes(token)))
            return Unauthorized();
        if ((await users.GetUsersInRoleAsync(RoleNames.Admin)).Count > 0) return Conflict(new ProblemDetails { Status = 409, Title = "An administrator already exists." });

        var user = new AppUser
        {
            UserName = req.Username.Trim(), FullName = req.FullName.Trim(), IsActive = true,
            CompanyAccess = string.Join(',', companies.All.Select(c => c.Key)),
        };
        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded) return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["password"] = created.Errors.Select(e => e.Description).ToArray() }));
        await users.AddToRoleAsync(user, RoleNames.Admin);
        return NoContent();
    }
}
