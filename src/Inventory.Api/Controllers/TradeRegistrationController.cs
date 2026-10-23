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
public class TradeRegistrationController(ICompanyScopes stores, IDataProtectionProvider protection, IConfiguration config) : ControllerBase
{
    private const string DeviceHeader = "X-Device-Token", SessionHeader = "X-Partner-Session", TrustHeader = "X-Trusted-Browser";
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

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>A partner on a browser they signed in from before is let straight in (no code); everyone else gets a code or carries on their application.</summary>
    [HttpPost("start"), EnableRateLimiting(Policies.ApplyRateLimit)]
    public async Task<IActionResult> Start(EmailInput i, CancellationToken ct)
    {
        var svc = Svc();
        if (await svc.IsTrustedAsync(i.Email, Request.Headers[TrustHeader].FirstOrDefault(), Ip, ct))
            return Ok(new { next = "signedin", session = Sessions.Protect(TradeRegistrationService.CleanEmail(i.Email), SessionLife) });
        return Ok(await svc.StartAsync(i.Email, Device, ct));
    }

    [HttpPost("signout")]
    public async Task<IActionResult> SignOut(CancellationToken ct) { await Svc().ForgetAsync(Request.Headers[TrustHeader].FirstOrDefault(), ct); return NoContent(); }

    [HttpPost("resend"), EnableRateLimiting(Policies.ApplyRateLimit)]
    public async Task<IActionResult> Resend(EmailInput i, CancellationToken ct) { await Svc().ResendAsync(i.Email, ct); return NoContent(); }

    /// <summary>A right code either confirms an application's email (the saved state comes back) or signs a partner in (a session comes back).</summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify(CodeInput i, CancellationToken ct)
    {
        var svc = Svc();
        var r = await svc.VerifyAsync(i.Email, i.Code, Device, ct);
        if (!r.Partner) return Ok(new { partner = false, deviceToken = r.DeviceToken, state = r.State });
        var trusted = await svc.TrustAsync(r.Email, Ip, Request.Headers.UserAgent.ToString(), ct);
        return Ok(new { partner = true, session = Sessions.Protect(r.Email, SessionLife), trustedBrowser = trusted });
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

    private string? SessionEmail()
    {
        try { return Sessions.Unprotect(Request.Headers[SessionHeader].FirstOrDefault() ?? ""); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
    private static ObjectResult SignInAgain() => new(new ProblemDetails { Status = 401, Title = "Please sign in again." }) { StatusCode = 401 };
    private string ReturnUrl => (config["Site:PublicUrl"] is { Length: > 0 } u ? u.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}") + "/?partner=paid#partner";

    /// <summary>Runs portal work in the store asked for, for the signed-in partner.</summary>
    private async Task<IActionResult> Portal<T>(string store, Func<PartnerPortalService, string, Task<T>> work)
    {
        if (SessionEmail() is not { } mail) return SignInAgain();
        if (!stores.TradeStores.Contains(store)) return NotFound();
        return Ok(await stores.InAsync(store, sp => work(sp.GetRequiredService<PartnerPortalService>(), mail)));
    }

    public sealed record OrderInput(List<PortalLine> Lines, string Pay, string? Note);
    public sealed record FundInput(decimal Amount);
    public sealed record MessageInput(string Text);

    [HttpPost("portal/{store}/order")]
    public Task<IActionResult> Order(string store, OrderInput i, CancellationToken ct) => Portal(store, (s, m) => s.OrderAsync(m, new PortalOrder(i.Lines ?? [], i.Pay, i.Note), ReturnUrl, ct));
    [HttpPost("portal/{store}/fund")]
    public Task<IActionResult> Fund(string store, FundInput i, CancellationToken ct) => Portal(store, (s, m) => s.FundAsync(m, i.Amount, ReturnUrl, ct));
    [HttpPost("portal/{store}/confirm")]
    public Task<IActionResult> Confirm(string store, CancellationToken ct) => Portal(store, async (s, m) => new { message = await s.ConfirmAsync(m, ct) });
    [HttpGet("portal/{store}/wallet")]
    public Task<IActionResult> Wallet(string store, CancellationToken ct) => Portal(store, (s, m) => s.WalletAsync(m, ct));
    [HttpGet("portal/{store}/messages")]
    public Task<IActionResult> Messages(string store, CancellationToken ct) => Portal(store, (s, m) => s.MessagesAsync(m, ct));
    [HttpPost("portal/{store}/messages")]
    public Task<IActionResult> Send(string store, MessageInput i, CancellationToken ct) => Portal(store, async (s, m) => { await s.SendAsync(m, i.Text, ct); return await s.MessagesAsync(m, ct); });
    [HttpGet("portal/{store}/notices")]
    public Task<IActionResult> Notices(string store, CancellationToken ct) => Portal(store, (s, m) => s.NoticesAsync(m, ct));

    /// <summary>The signed-in partner's accounts, one per store where their email is registered.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (SessionEmail() is not { } mail) return SignInAgain();
        var accounts = new List<PartnerAccount>();
        foreach (var key in stores.TradeStores)
            if (await stores.InAsync(key, sp => sp.GetRequiredService<PartnerDashboard>().ForEmailAsync(mail, stores.DisplayName(key), ct)) is { } a) accounts.Add(a);
        if (accounts.Count == 0) return Unauthorized(new ProblemDetails { Status = 401, Title = "This account no longer exists." });
        return Ok(new { email = TradeRegistrationService.Mask(mail), accounts });
    }
}
