using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.AppsEngine;
using Inventory.Application.TimeOff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

public sealed record StatusRequest(string Status);

/// <summary>
/// The record-keeping apps this business has bought (Sign, Events, Fleet…): one set of endpoints, driven by each app's definition. Administrators only
/// (this installation has two roles), and only for apps listed in <see cref="AppAccess"/>.
/// </summary>
[ApiController, Route("api/apps"), Authorize(Policy = Policies.Marketing)]
public class AppsRecordsController(ICurrentUser cu, AppRecordService svc, ICompanyContext company, AppAccess access, FiguresService figures, IBusinessDbContext db) : AppController(cu)
{
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}/api/site/{company.Key}";
    private bool Marketer => AppRecordService.IsMarketer(Me);

    /// <summary>Null when the app is part of this installation (and, for a digital marketer, is a Marketing app); otherwise the refusal to send back.</summary>
    private IActionResult? Refuse(string app)
    {
        AppRecordService.Def(app);   // not one of the apps at all: 404
        if (Marketer && !AppAccess.Marketing.Contains(app)) return StatusCode(403, new ProblemDetails { Status = 403, Title = "Your login is for marketing only." });
        return access.For(company.HasNewFeatures, company.HasMarketing).IsEnabled(app) ? null : StatusCode(403, new ProblemDetails { Status = 403, Title = "This app isn't part of your plan.", Detail = "Ask SpringUpTech to add it." });
    }

    /// <summary>The apps switched on here, for the menu (a digital marketer sees the Marketing apps only).</summary>
    [HttpGet("enabled")]
    public IActionResult Enabled() => Ok(access.For(company.HasNewFeatures, company.HasMarketing).Enabled.Where(a => !Marketer || AppAccess.Marketing.Contains(a)));

    /// <summary>Names to pick from in a form (customers for a broadcast, products for a post): id and name only, never balances, prices or costs.</summary>
    [HttpGet("options/{kind}")]
    public async Task<IActionResult> Options(string kind, CancellationToken ct) => kind switch
    {
        "customer" => Ok(await db.Customers.AsNoTracking().OrderBy(c => c.Name).Take(5000)
            .Select(c => new { c.Id, Name = c.Phone != null && c.Phone.Length > 4 ? c.Name + " · …" + c.Phone.Substring(c.Phone.Length - 4) : c.Name, c.MarketingStatus }).ToListAsync(ct)),
        "product" => Ok(await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Take(2000).Select(p => new { p.Id, Name = p.Name + " (" + p.Sku + ")" }).ToListAsync(ct)),
        _ => NotFound(),
    };

    /// <summary>What digital marketers have asked to put live, waiting for an admin.</summary>
    [HttpGet("approvals"), Authorize(Policy = Policies.Admin)]
    public Task<List<ApprovalDto>> Approvals(CancellationToken ct) => svc.ApprovalsAsync(ct);

    [HttpPost("{app}/records/{id:int}/approve"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Approve(string app, int id, CancellationToken ct) => Refuse(app) ?? Ok(await svc.ApproveAsync(app, id, Me, BaseUrl, ct));

    [HttpPost("{app}/records/{id:int}/reject"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Reject(string app, int id, ActionInput input, CancellationToken ct)
    {
        if (Refuse(app) is { } no) return no;
        await svc.RejectAsync(app, id, input.Text, Me, ct);
        return NoContent();
    }

    [HttpGet("spreadsheet/figures")]
    public async Task<IActionResult> Figures(CancellationToken ct) =>
        Refuse("spreadsheet") ?? Ok(new { names = FiguresService.Names.Select(n => new { n.Name, n.Label }), values = await figures.AllAsync(ct) });

    [HttpGet("{app}")]
    public async Task<IActionResult> Overview(string app, CancellationToken ct) => Refuse(app) ?? Ok(await svc.OverviewAsync(app, Me, BaseUrl, ct));

    [HttpGet("{app}/{kind}")]
    public async Task<IActionResult> List(string app, string kind, [FromQuery] int? parent, [FromQuery] string? status, [FromQuery] string? q, CancellationToken ct) =>
        Refuse(app) ?? Ok(await svc.ListAsync(app, kind, parent, status, q, Me, BaseUrl, ct));

    [HttpGet("{app}/records/{id:int}")]
    public async Task<IActionResult> Get(string app, int id, CancellationToken ct) => Refuse(app) ?? Ok(await svc.GetAsync(app, id, Me, BaseUrl, ct));

    [HttpPost("{app}/{kind}")]
    public async Task<IActionResult> Create(string app, string kind, SaveInput input, CancellationToken ct) =>
        Refuse(app) ?? Ok(new { id = await svc.SaveAsync(app, kind, null, input, Me, BaseUrl, ct) });

    [HttpPut("{app}/{kind}/{id:int}")]
    public async Task<IActionResult> Update(string app, string kind, int id, SaveInput input, CancellationToken ct) =>
        Refuse(app) ?? Ok(new { id = await svc.SaveAsync(app, kind, id, input, Me, BaseUrl, ct) });

    [HttpPost("{app}/records/{id:int}/status")]
    public async Task<IActionResult> Status(string app, int id, StatusRequest r, CancellationToken ct)
    {
        if (Refuse(app) is { } no) return no;
        await svc.SetStatusAsync(app, id, r.Status, Me, BaseUrl, ct);
        return NoContent();
    }

    [HttpPost("{app}/records/{id:int}/actions/{key}")]
    public async Task<IActionResult> Run(string app, int id, string key, ActionInput input, CancellationToken ct) =>
        Refuse(app) ?? Ok(await svc.ActionAsync(app, id, key, input.Text, Me, BaseUrl, ct));

    [HttpDelete("{app}/records/{id:int}")]
    public async Task<IActionResult> Delete(string app, int id, CancellationToken ct)
    {
        if (Refuse(app) is { } no) return no;
        await svc.DeleteAsync(app, id, Me, BaseUrl, ct);
        return NoContent();
    }
}

/// <summary>Time Off: leave types, requests, approval and balances.</summary>
[ApiController, Route("api/time-off"), Authorize(Policy = Policies.Admin), RequiresNewFeatures]
public class TimeOffController(ICurrentUser cu, TimeOffService svc, AppAccess access) : AppController(cu)
{
    private IActionResult? Refuse() => access.IsEnabled("time-off") ? null : StatusCode(403, new ProblemDetails { Status = 403, Title = "Time Off isn't part of your plan.", Detail = "Ask SpringUpTech to add it." });

    [HttpGet]
    public async Task<IActionResult> Overview([FromQuery] int? year, [FromQuery] string? status, CancellationToken ct) => Refuse() ?? Ok(await svc.OverviewAsync(year, status, ct));

    [HttpPost("types")]
    public async Task<IActionResult> SaveType(LeaveTypeInput i, CancellationToken ct) => Refuse() ?? Ok(new { id = await svc.SaveTypeAsync(i, Me, ct) });

    [HttpPost("requests")]
    public async Task<IActionResult> Request(LeaveRequestInput i, CancellationToken ct) => Refuse() ?? Ok(new { id = await svc.RequestAsync(i, Me, ct) });

    [HttpPost("requests/{id:int}/decision")]
    public async Task<IActionResult> Decide(int id, DecisionInput i, CancellationToken ct)
    {
        if (Refuse() is { } no) return no;
        await svc.DecideAsync(id, i, Me, ct);
        return NoContent();
    }

    [HttpPost("requests/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        if (Refuse() is { } no) return no;
        await svc.CancelAsync(id, Me, ct);
        return NoContent();
    }

    [HttpGet("unpaid/{year:int}/{month:int}")]
    public async Task<IActionResult> Unpaid(int year, int month, CancellationToken ct) => Refuse() ?? Ok(await svc.UnpaidAsync(year, month, ct));
}
