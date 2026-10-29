using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Documents;
using Inventory.Application.Ledger;
using Inventory.Domain;
using Inventory.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public sealed record PeriodRequest(string Status, string? Note);
public sealed record ReverseRequest(string Reason);
public sealed record ReconciliationDto(decimal LedgerInventory, decimal StockAtCost, decimal Difference);

/// <summary>The general ledger: chart of accounts, statements, journals (prepared by one person, approved by another) and month close.</summary>
[ApiController, Route("api/gl"), ServiceFilter(typeof(LedgerSyncFilter)), RequiresNewFeatures]
public class AccountingController(ICurrentUser cu, AccountingService svc, IClock clock, DocumentQueries docs, IDocumentRenderer renderer, IBusinessDbContext db, ICompanyContext company)
    : AppController(cu)
{
    private DateOnly Today => clock.BusinessToday;

    /// <summary>
    /// The statements as a file to keep or send to an accountant: profit and loss for the period, balance sheet and trial balance at its end.
    /// which = all | pl | bs | tb; format = pdf | xlsx.
    /// </summary>
    [HttpGet("statements"), Authorize(Policy = Perm.GlView)]
    public async Task<IActionResult> Statements([FromQuery] string? which, [FromQuery] string? format, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int? branchId, CancellationToken ct)
    {
        which = which is "pl" or "bs" or "tb" ? which : "all";
        var end = to ?? Today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (start > end) throw new BusinessRuleException("The start date is after the end date.");
        await svc.ForBranchAsync(branchId, ct);
        var branch = branchId is int b ? await db.Branches.AsNoTracking().Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var d = new StatementsDoc(await docs.BrandAsync(ct), which, start, end, branch,
            await svc.ProfitAndLossAsync(start, end, ct), await svc.BalanceSheetAsync(end, ct), await svc.TrialBalanceAsync(end, ct), clock.BusinessNow);

        var title = which switch { "pl" => "profit-and-loss", "bs" => "balance-sheet", "tb" => "trial-balance", _ => "financial-statements" };
        var name = $"{company.Key}-{title}-{(which is "pl" or "all" ? $"{start:yyyy-MM-dd}-to-" : "")}{end:yyyy-MM-dd}";
        Response.Headers.CacheControl = "no-store";
        if (format == "xlsx") return File(ExcelExporter.Statements(d), ExcelExporter.ContentType, name + ".xlsx");
        Response.Headers.ContentDisposition = $"inline; filename=\"{name}.pdf\"";
        return File(renderer.Statements(d), "application/pdf");
    }

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
