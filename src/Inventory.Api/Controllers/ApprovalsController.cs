using Inventory.Domain;
using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Approvals;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public sealed record DecideRequest(string? Note);

/// <summary>The approvals inbox: actions held because they were worth more than the business's limit, decided by someone other than the requester.</summary>
[ApiController, Route("api/approvals"), RequiresNewFeatures]
public class ApprovalsController(ICurrentUser cu, ApprovalService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Admin)]
    public Task<IReadOnlyList<ApprovalDto>> List([FromQuery] string? status, CancellationToken ct) => svc.ListAsync(status ?? "Pending", null, ct);

    /// <summary>Anyone signed in can follow what they themselves asked for.</summary>
    [HttpGet("mine"), Authorize]
    public Task<IReadOnlyList<ApprovalDto>> Mine(CancellationToken ct) => svc.ListAsync(null, Me.Id, ct);

    [HttpPost("{id:int}/approve"), Authorize(Policy = Policies.Admin)]
    public Task<ApprovalDto> Approve(int id, DecideRequest r, CancellationToken ct) => svc.DecideAsync(id, true, r.Note, Me, ct);

    [HttpPost("{id:int}/reject"), Authorize(Policy = Policies.Admin)]
    public Task<ApprovalDto> Reject(int id, DecideRequest r, CancellationToken ct) => svc.DecideAsync(id, false, r.Note, Me, ct);

    [HttpGet("rules"), Authorize(Policy = Policies.Admin)]
    public Task<IReadOnlyList<ApprovalRuleDto>> Rules(CancellationToken ct) => svc.RulesAsync(ct);

    /// <summary>Setting the limits is a business setting, so it needs company.edit, not just the right to approve.</summary>
    [HttpPut("rules"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SetRule(ApprovalRuleDto rule, CancellationToken ct)
    {
        await svc.SetRuleAsync(rule, Me, ct);
        return NoContent();
    }
}
