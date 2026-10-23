using Inventory.Domain;
using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Banking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public sealed record MatchRequest(long GlLineId);
public sealed record CompleteRequest(DateOnly StatementDate, decimal StatementBalance);

/// <summary>Bank accounts, statement import, matching and reconciliation.</summary>
[ApiController, Route("api/banking"), ServiceFilter(typeof(LedgerSyncFilter)), Authorize(Policy = Perm.BankReconcile), RequiresNewFeatures]
public class BankingController(ICurrentUser cu, BankReconciliationService svc) : AppController(cu)
{
    [HttpGet("accounts")]
    public Task<List<BankAccountDto>> Accounts(CancellationToken ct) => svc.AccountsAsync(ct);

    [HttpPost("accounts")]
    public async Task<IActionResult> Add(BankAccountInput i, CancellationToken ct) => Ok(new { id = await svc.AddAccountAsync(i, Me, ct) });

    [HttpPut("accounts/{id:int}")]
    public async Task<IActionResult> Update(int id, BankAccountInput i, CancellationToken ct) { await svc.UpdateAccountAsync(id, i, Me, ct); return NoContent(); }

    /// <summary>The statement as a CSV file upload (multipart, field "file"), up to 5 MB.</summary>
    [HttpPost("accounts/{id:int}/import"), RequestSizeLimit(5_000_000)]
    public async Task<ImportResult> Import(int id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new Inventory.Application.Common.BusinessRuleException("Choose the statement file (CSV).");
        using var reader = new StreamReader(file.OpenReadStream());
        return await svc.ImportCsvAsync(id, await reader.ReadToEndAsync(ct), Me, ct);
    }

    [HttpPost("accounts/{id:int}/auto-match")]
    public async Task<IActionResult> AutoMatch(int id, CancellationToken ct) => Ok(new { matched = await svc.AutoMatchAsync(id, ct) });

    [HttpGet("accounts/{id:int}/reconcile")]
    public Task<ReconcileViewDto> View(int id, [FromQuery] DateOnly? asOf, [FromQuery] decimal? statementBalance, CancellationToken ct) => svc.ViewAsync(id, asOf, statementBalance, ct);

    [HttpPost("accounts/{id:int}/reconcile")]
    public async Task<IActionResult> Complete(int id, CompleteRequest r, CancellationToken ct) => Ok(new { id = await svc.CompleteAsync(id, r.StatementDate, r.StatementBalance, Me, ct) });

    [HttpPost("lines/{lineId:long}/match")]
    public async Task<IActionResult> Match(long lineId, MatchRequest r, CancellationToken ct) { await svc.MatchAsync(lineId, r.GlLineId, Me, ct); return NoContent(); }

    [HttpPost("lines/{lineId:long}/unmatch")]
    public async Task<IActionResult> Unmatch(long lineId, CancellationToken ct) { await svc.UnmatchAsync(lineId, Me, ct); return NoContent(); }

    [HttpPost("lines/{lineId:long}/ignore")]
    public async Task<IActionResult> Ignore(long lineId, CancellationToken ct) { await svc.IgnoreAsync(lineId, Me, ct); return NoContent(); }

    [HttpPost("lines/{lineId:long}/post")]
    public async Task<IActionResult> Post(long lineId, PostFromStatementInput i, CancellationToken ct) => Ok(new { journalId = await svc.PostFromStatementAsync(lineId, i, Me, ct) });
}
