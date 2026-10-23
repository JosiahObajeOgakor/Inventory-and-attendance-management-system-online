using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Assets;
using Inventory.Application.Budgets;
using Inventory.Application.Currencies;
using Inventory.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public sealed record RunDepreciationRequest(int Year, int Month);
public sealed record CurrencyRequest(string Code, string? Name, string? Symbol);
public sealed record FxRateRequest(DateOnly Date, decimal Rate, string? Source);
public sealed record RevalueRequest(DateOnly AsOf);

/// <summary>Fixed-asset register, depreciation runs and disposals.</summary>
[ApiController, Route("api/assets"), ServiceFilter(typeof(LedgerSyncFilter)), RequiresNewFeatures]
public class FixedAssetsController(ICurrentUser cu, FixedAssetService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Perm.GlView)]
    public async Task<IActionResult> Register(CancellationToken ct)
    {
        var r = await svc.RegisterAsync(ct);
        return Ok(new { r.Assets, r.TotalCost, r.TotalAccumulated, r.TotalBookValue, r.Runs,
            byCategory = r.ByCategory.Select(c => new { category = c.Category, count = c.Count, cost = c.Cost, bookValue = c.BookValue }), categories = FixedAssetService.Categories });
    }

    [HttpPost, Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Add(NewAssetInput i, CancellationToken ct) => Ok(new { id = await svc.AddAsync(i, Me, ct) });

    [HttpGet("depreciation/{year:int}/{month:int}"), Authorize(Policy = Perm.GlView)]
    public async Task<IActionResult> Preview(int year, int month, CancellationToken ct)
    {
        var p = await svc.PreviewAsync(year, month, ct);
        return Ok(new { p.Year, p.Month, p.AlreadyRun, p.Total, lines = p.Lines.Select(l => new { assetId = l.AssetId, code = l.Code, name = l.Name, amount = l.Amount }) });
    }

    [HttpPost("depreciation"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Run(RunDepreciationRequest r, CancellationToken ct) => Ok(new { id = await svc.RunAsync(r.Year, r.Month, Me, ct) });

    [HttpPost("{id:int}/dispose"), Authorize(Policy = Perm.GlJournalApprove)]
    public async Task<IActionResult> Dispose(int id, DisposeInput i, CancellationToken ct) { await svc.DisposeAsync(id, i, Me, ct); return NoContent(); }

    [HttpGet("{id:int}/schedule"), Authorize(Policy = Perm.GlView)]
    public async Task<IActionResult> Schedule(int id, CancellationToken ct) =>
        Ok((await svc.ScheduleAsync(id, ct)).Select(s => new { year = s.Year, month = s.Month, amount = s.Amount }));
}

/// <summary>Budgets and budget-versus-actual.</summary>
[ApiController, Route("api/budgets"), ServiceFilter(typeof(LedgerSyncFilter)), RequiresNewFeatures]
public class BudgetsController(ICurrentUser cu, BudgetService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Perm.GlView)]
    public Task<List<BudgetSummaryDto>> List(CancellationToken ct) => svc.ListAsync(ct);

    [HttpGet("{id:int}"), Authorize(Policy = Perm.GlView)]
    public Task<BudgetDto> Get(int id, CancellationToken ct) => svc.GetAsync(id, ct);

    [HttpPost, Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Create(NewBudgetInput i, CancellationToken ct) => Ok(new { id = await svc.CreateAsync(i, Me, ct) });

    [HttpPut("{id:int}/rows"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Rows(int id, List<BudgetRowInput> rows, CancellationToken ct) { await svc.SaveRowsAsync(id, rows, Me, ct); return NoContent(); }

    [HttpPost("{id:int}/approve"), Authorize(Policy = Perm.GlJournalApprove)]
    public async Task<IActionResult> Approve(int id, CancellationToken ct) { await svc.SetStatusAsync(id, true, Me, ct); return NoContent(); }

    [HttpPost("{id:int}/reopen"), Authorize(Policy = Perm.GlJournalApprove)]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct) { await svc.SetStatusAsync(id, false, Me, ct); return NoContent(); }

    [HttpDelete("{id:int}"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, Me, ct); return NoContent(); }

    [HttpGet("{id:int}/variance"), Authorize(Policy = Perm.GlView)]
    public Task<VarianceDto> Variance(int id, [FromQuery] int toMonth, CancellationToken ct) => svc.VarianceAsync(id, toMonth is >= 1 and <= 12 ? toMonth : 12, ct);
}

/// <summary>Currencies, exchange rates, foreign bills and invoices, settlements and revaluation.</summary>
[ApiController, Route("api/currencies"), ServiceFilter(typeof(LedgerSyncFilter)), RequiresNewFeatures]
public class CurrenciesController(ICurrentUser cu, CurrencyService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Perm.GlView)]
    public Task<List<CurrencyDto>> List(CancellationToken ct) => svc.CurrenciesAsync(ct);

    [HttpPost, Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Add(CurrencyRequest r, CancellationToken ct) { await svc.AddCurrencyAsync(r.Code, r.Name, r.Symbol, Me, ct); return NoContent(); }

    [HttpGet("{code}/rates"), Authorize(Policy = Perm.GlView)]
    public Task<List<RateDto>> Rates(string code, CancellationToken ct) => svc.RatesAsync(code.ToUpperInvariant(), ct);

    [HttpPut("{code}/rates"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> SetRate(string code, FxRateRequest r, CancellationToken ct) { await svc.SetRateAsync(code.ToUpperInvariant(), r.Date, r.Rate, r.Source, Me, ct); return NoContent(); }

    [HttpGet("documents"), Authorize(Policy = Perm.GlView)]
    public Task<List<FxDocumentDto>> Documents([FromQuery] bool openOnly, CancellationToken ct) => svc.DocumentsAsync(openOnly, ct);

    [HttpPost("documents"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Book(NewFxDocumentInput i, CancellationToken ct) => Ok(new { id = await svc.CreateDocumentAsync(i, Me, ct) });

    [HttpPost("documents/{id:int}/settle"), Authorize(Policy = Perm.GlJournalCreate)]
    public async Task<IActionResult> Settle(int id, SettleInput i, CancellationToken ct) => Ok(new { id = await svc.SettleAsync(id, i, Me, ct) });

    [HttpGet("revaluation"), Authorize(Policy = Perm.GlView)]
    public Task<RevaluationDto> Revaluation([FromQuery] DateOnly asOf, CancellationToken ct) => svc.RevaluationAsync(asOf, ct);

    [HttpPost("revaluation"), Authorize(Policy = Perm.GlJournalApprove)]
    public async Task<IActionResult> Revalue(RevalueRequest r, CancellationToken ct) { await svc.PostRevaluationAsync(r.AsOf, Me, ct); return NoContent(); }
}

