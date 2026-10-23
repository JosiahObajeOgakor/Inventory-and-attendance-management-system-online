using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Partners;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

/// <summary>Managers review trade applications (with the CAC check and the uploaded documents), approve at a tier, reject, suspend, reinstate or delete.</summary>
[ApiController, Route("api/trade-partners"), Authorize(Policy = Perm.PartnersManage)]
public class TradePartnersController(ICurrentUser cu, TradePartnerService svc) : AppController(cu)
{
    [HttpGet] public Task<List<TradePartnerDto>> List([FromQuery] string? status, CancellationToken ct) => svc.ListAsync(string.IsNullOrWhiteSpace(status) ? null : status, ct);

    [HttpGet("{id:int}/documents/{docId:int}")]
    public async Task<IActionResult> Document(int id, int docId, CancellationToken ct) =>
        await svc.DocumentAsync(id, docId, ct) is { } d ? File(d.Data, d.ContentType, d.FileName) : NotFound();

    [HttpPost("{id:int}/approve")] public async Task<IActionResult> Approve(int id, Decision d, CancellationToken ct) { await svc.ApproveAsync(id, d.Tier ?? "", d.Note, Me, ct); return NoContent(); }
    [HttpPost("{id:int}/reject")] public async Task<IActionResult> Reject(int id, Decision d, CancellationToken ct) { await svc.RejectAsync(id, d.Note, Me, ct); return NoContent(); }
    [HttpPost("{id:int}/suspend")] public async Task<IActionResult> Suspend(int id, Decision d, CancellationToken ct) { await svc.SuspendAsync(id, true, d.Note, Me, ct); return NoContent(); }
    [HttpPost("{id:int}/reinstate")] public async Task<IActionResult> Reinstate(int id, Decision d, CancellationToken ct) { await svc.SuspendAsync(id, false, d.Note, Me, ct); return NoContent(); }
    [HttpPost("{id:int}/reverify")] public async Task<IActionResult> Reverify(int id, CancellationToken ct) => Ok(new { verification = await svc.ReverifyAsync(id, Me, ct) });
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, Me, ct); return NoContent(); }

    /// <summary>The conversation with a partner (their portal chat), and a reply that reaches them by email and WhatsApp.</summary>
    [HttpGet("{id:int}/messages")] public Task<List<PortalMessage>> Thread(int id, [FromServices] PartnerPortalService portal, CancellationToken ct) => portal.ThreadAsync(id, ct);
    [HttpPost("{id:int}/messages")]
    public async Task<List<PortalMessage>> Reply(int id, MessageInput m, [FromServices] PartnerPortalService portal, CancellationToken ct) { await portal.ReplyAsync(id, m.Text, Me, ct); return await portal.ThreadAsync(id, ct); }

    public sealed record Decision(string? Tier, string? Note);
    public sealed record MessageInput(string Text);
}
