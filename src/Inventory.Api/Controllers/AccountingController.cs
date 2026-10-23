using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Ledger;
using Inventory.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public sealed record PeriodRequest(string Status, string? Note);
public sealed record ReverseRequest(string Reason);
public sealed record ReconciliationDto(decimal LedgerInventory, decimal StockAtCost, decimal Difference);

/// <summary>The general ledger: chart of accounts, statements, journals (prepared by one person, approved by another) and month close.</summary>
[ApiController, Route("api/gl"), ServiceFilter(typeof(LedgerSyncFilter)), RequiresNewFeatures]
public class AccountingController(ICurrentUser cu, AccountingService svc, IClock clock) : AppController(cu)
{
    private DateOnly Today => clock.BusinessToday;

    [HttpGet("accounts"), Authorize(Policy = Perm.GlView)]
    public Task<IReadOnlyList<AccountDto>> Accounts(CancellationToken ct) => svc.AccountsAsync(ct);

    [HttpPost("accounts"), Authorize(Policy = Perm.GlPeriodClose)]
    public async Task<IActionResult> AddAccount(AccountInput i, CancellationToken ct) => Ok(new { id = await svc.AddAccountAsync(i, Me, ct) });

    [HttpGet("trial-balance"), Authorize(Policy = Perm.GlView)]
    public async Task<TrialBalanceDto> TrialBalance([FromQuery] DateOnly? asOf, [FromQuery] int? branchId, CancellationToken ct)
    { await svc.ForBranchAsync(branchId, ct); return await svc.TrialBalanceAsync(asOf ?? Today, ct); }

    [HttpGet("profit-and-loss"), Authorize(Policy = Perm.GlView)]
    public async Task<ProfitAndLossDto> ProfitAndLoss([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? branchId, CancellationToken ct)
    {
        await svc.ForBranchAsync(branchId, ct);
        return await
            svc.ProfitAndLossAsync(from ?? new DateOnly(Today.Year, Today.Month, 1), to ?? Today, ct);
    }

    [HttpGet("balance-sheet"), Authorize(Policy = Perm.GlView)]
    public async Task<BalanceSheetDto> BalanceSheet([FromQuery] DateOnly? asOf, [FromQuery] int? branchId, CancellationToken ct)
    { await svc.ForBranchAsync(branchId, ct); return await svc.BalanceSheetAsync(asOf ?? Today, ct); }

    [HttpGet("accounts/{code}/ledger"), Authorize(Policy = Perm.GlView)]
    public Task<IReadOnlyList<LedgerLineDto>> AccountLedger(string code, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        svc.AccountLedgerAsync(code, from ?? Today.AddMonths(-1), to ?? Today, ct);

    [HttpGet("journals"), Authorize(Policy = Perm.GlView)]
    public Task<IReadOnlyList<JournalDto>> Journals([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? status, CancellationToken ct) =>
        svc.JournalsAsync(from ?? Today.AddMonths(-1), to ?? Today, status, ct);

    [HttpPost("journals"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Draft(ManualJournalInput i, CancellationToken ct) => Ok(new { id = await svc.DraftAsync(i, Me, ct) });

    [HttpPost("journals/{id:long}/approve"), Authorize(Policy = Perm.GlJournalApprove)]
    public async Task<IActionResult> Approve(long id, CancellationToken ct)
    {
        await svc.ApproveAsync(id, Me, true, ct);
        return NoContent();
    }

    [HttpPost("journals/{id:long}/reverse"), Authorize(Policy = Perm.GlJournalApprove)]
    public async Task<IActionResult> Reverse(long id, ReverseRequest r, CancellationToken ct)
    {
        await svc.ReverseManualAsync(id, r.Reason, Me, ct);
        return NoContent();
    }

    [HttpGet("periods"), Authorize(Policy = Perm.GlView)]
    public Task<IReadOnlyList<PeriodDto>> Periods(CancellationToken ct) => svc.PeriodsAsync(ct);

    [HttpPut("periods/{year:int}/{month:int}"), Authorize(Policy = Perm.GlPeriodClose)]
    public async Task<IActionResult> SetPeriod(int year, int month, PeriodRequest r, CancellationToken ct)
    {
        await svc.SetPeriodAsync(year, month, r.Status, r.Note, Me, ct);
        return NoContent();
    }

    [HttpPost("opening-balances"), Authorize(Policy = Perm.GlPeriodClose)]
    public async Task<IActionResult> Opening(OpeningBalancesInput i, CancellationToken ct) => Ok(new { journalId = await svc.PostOpeningBalancesAsync(i, Me, ct) });

    [HttpGet("inventory-reconciliation"), Authorize(Policy = Perm.GlView)]
    public async Task<ReconciliationDto> Reconcile(CancellationToken ct)
    {
        var (ledger, stock) = await svc.InventoryReconciliationAsync(ct);
        return new ReconciliationDto(ledger, stock, ledger - stock);
    }
}
