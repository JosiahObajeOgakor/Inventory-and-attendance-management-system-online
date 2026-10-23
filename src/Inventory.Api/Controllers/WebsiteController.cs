using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Website;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

/// <summary>The public website's content that staff manage: reviews visitors left (approve or reject) and partner logos (shown only with permission).</summary>
[ApiController, Route("api/website"), Authorize(Policy = Policies.Marketing), RequiresNewFeatures(Marketing = true)]
public class WebsiteController(ICurrentUser cu, WebsiteService svc) : AppController(cu)
{
    [HttpGet("reviews")] public Task<List<ReviewDto>> Reviews([FromQuery] string? status, CancellationToken ct) => svc.ReviewsAsync(string.IsNullOrWhiteSpace(status) ? null : status, ct);
    [HttpPost("reviews/{id:int}/approve")] public async Task<IActionResult> Approve(int id, CancellationToken ct) { await svc.DecideReviewAsync(id, true, Me, ct); return NoContent(); }
    [HttpPost("reviews/{id:int}/reject")] public async Task<IActionResult> Reject(int id, CancellationToken ct) { await svc.DecideReviewAsync(id, false, Me, ct); return NoContent(); }

    [HttpGet("partners")] public Task<List<PartnerDto>> Partners(CancellationToken ct) => svc.PartnersAsync(ct);

    [HttpPost("partners"), RequestSizeLimit(1_100_000)]
    public async Task<IActionResult> AddPartner([FromForm] PartnerForm f, CancellationToken ct) => Ok(new { id = await svc.SavePartnerAsync(null, f.Input, await BytesAsync(f.Logo, ct), Me, ct) });

    [HttpPut("partners/{id:int}"), RequestSizeLimit(1_100_000)]
    public async Task<IActionResult> UpdatePartner(int id, [FromForm] PartnerForm f, CancellationToken ct) { await svc.SavePartnerAsync(id, f.Input, await BytesAsync(f.Logo, ct), Me, ct); return NoContent(); }

    [HttpDelete("partners/{id:int}")] public async Task<IActionResult> DeletePartner(int id, CancellationToken ct) { await svc.DeletePartnerAsync(id, Me, ct); return NoContent(); }

    [HttpGet("partners/{id:int}/logo")]
    public async Task<IActionResult> Logo(int id, CancellationToken ct) => await svc.PartnerLogoAsync(id, false, ct) is { } l ? File(l.Data, l.ContentType) : NotFound();

    private static async Task<byte[]?> BytesAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return null;
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public sealed class PartnerForm
    {
        public string Name { get; set; } = "";
        public string? Url { get; set; }
        public bool PermissionGiven { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public IFormFile? Logo { get; set; }
        public PartnerInput Input => new(Name, Url, PermissionGiven, IsActive, SortOrder);
    }
}