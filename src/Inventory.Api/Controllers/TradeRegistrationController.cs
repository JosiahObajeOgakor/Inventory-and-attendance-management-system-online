using System.Text.Json.Nodes;
using Inventory.Api.Infrastructure;
using Inventory.Application.Common;
using Inventory.Application.Partners;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Inventory.Api.Controllers;

/// <summary>
/// The website's step-by-step trade registration and the partner sign-in. No login: the applicant's device carries a random token (X-Device-Token) that
/// identifies their saved application; a partner proves their email with a code and gets a short-lived signed session (X-Partner-Session).
/// Drafts are kept in the first trade store; the finished application goes to every store chosen.
/// </summary>
[ApiController, Route("api/trade"), AllowAnonymous, EnableRateLimiting(Policies.WebChatRateLimit)]
public class TradeRegistrationController(ICompanyScopes stores, IDataProtectionProvider protection) : ControllerBase
{
    private const string DeviceHeader = "X-Device-Token", SessionHeader = "X-Partner-Session";
    private static readonly TimeSpan SessionLife = TimeSpan.FromHours(12);
    private ITimeLimitedDataProtector Sessions => protection.CreateProtector("partner-session").ToTimeLimitedDataProtector();
    private string? Device => Request.Headers[DeviceHeader].FirstOrDefault();

    /// <summary>Every call works in the store that holds the drafts.</summary>
    private TradeRegistrationService Svc()
    {
        HttpContext.Items[RequestCompany.OverrideItem] = stores.TradeStores[0];
        return HttpContext.RequestServices.GetRequiredService<TradeRegistrationService>();
    }

    public sealed record EmailInput(string? Email);
    public sealed record CodeInput(string? Email, string? Code);

    [HttpPost("start"), EnableRateLimiting(Policies.ApplyRateLimit)]
    public async Task<ActionResult<RegStart>> Start(EmailInput i, CancellationToken ct) => await Svc().StartAsync(i.Email, Device, ct);

    [HttpPost("resend"), EnableRateLimiting(Policies.ApplyRateLimit)]
    public async Task<IActionResult> Resend(EmailInput i, CancellationToken ct) { await Svc().ResendAsync(i.Email, ct); return NoContent(); }

    /// <summary>A right code either confirms an application's email (the saved state comes back) or signs a partner in (a session comes back).</summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify(CodeInput i, CancellationToken ct)
    {
        var r = await Svc().VerifyAsync(i.Email, i.Code, Device, ct);
        if (!r.Partner) return Ok(new { partner = false, deviceToken = r.DeviceToken, state = r.State });
        return Ok(new { partner = true, session = Sessions.Protect(r.Email, SessionLife) });
    }

    [HttpGet("draft")]
    public async Task<IActionResult> Draft(CancellationToken ct) => await Svc().ResumeAsync(Device, ct) is { } s ? Ok(s) : NoContent();

    [HttpPut("draft/step/{step:int}")]
    public async Task<RegState> Step(int step, JsonObject fields, CancellationToken ct) => await Svc().SaveStepAsync(Device, step, fields, ct);

    [HttpPost("draft/files"), RequestSizeLimit(3_500_000)]
    public async Task<ActionResult<RegState>> Upload([FromForm] string kind, IFormFile file, CancellationToken ct)
    {
        if (file.Length > TradePartnerService.MaxDocumentBytes) return Problem(statusCode: 400, title: "Please attach a file under 3 MB.");
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await Svc().UploadAsync(Device, kind, file.FileName, file.ContentType, ms.ToArray(), ct);
    }

    [HttpDelete("draft/files/{id:int}")]
    public async Task<RegState> RemoveFile(int id, CancellationToken ct) => await Svc().RemoveFileAsync(Device, id, ct);

    [HttpPost("draft/documents-done")]
    public async Task<RegState> DocumentsDone(CancellationToken ct) => await Svc().DocumentsDoneAsync(Device, ct);

    [HttpPost("draft/submit"), EnableRateLimiting(Policies.ApplyRateLimit)]
    public async Task<RegSubmitted> Submit(CancellationToken ct) => await Svc().SubmitAsync(Device, ct);

    /// <summary>The signed-in partner's accounts, one per store where their email is registered.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        string mail;
        try { mail = Sessions.Unprotect(Request.Headers[SessionHeader].FirstOrDefault() ?? ""); }
        catch (System.Security.Cryptography.CryptographicException) { return Unauthorized(new ProblemDetails { Status = 401, Title = "Please sign in again." }); }
        var accounts = new List<PartnerAccount>();
        foreach (var key in stores.TradeStores)
            if (await stores.InAsync(key, sp => sp.GetRequiredService<PartnerDashboard>().ForEmailAsync(mail, stores.DisplayName(key), ct)) is { } a) accounts.Add(a);
        if (accounts.Count == 0) return Unauthorized(new ProblemDetails { Status = 401, Title = "This account no longer exists." });
        return Ok(new { email = TradeRegistrationService.Mask(mail), accounts });
    }
}
