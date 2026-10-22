using System.Text;
using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Tax;
using Inventory.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

/// <summary>The monthly VAT return: figures, CSV for the filing, and recording the filing and payment.</summary>
[ApiController, Route("api/tax"), ServiceFilter(typeof(LedgerSyncFilter)), Authorize(Policy = Policies.Admin), RequiresNewFeatures]
public class TaxController(ICurrentUser cu, TaxService svc) : AppController(cu)
{
    [HttpGet("vat/{year:int}/{month:int}")]
    public Task<VatReturnDto> Vat(int year, int month, CancellationToken ct) => svc.VatReturnAsync(year, month, ct);

    [HttpGet("vat/{year:int}/{month:int}/return.csv")]
    public async Task<IActionResult> VatCsv(int year, int month, CancellationToken ct) => Csv(await svc.VatCsvAsync(year, month, ct), $"vat-{year}-{month:00}.csv");

    [HttpPost("vat/file")]
    public async Task<IActionResult> FileVat(FileVatInput i, CancellationToken ct) => Ok(new { id = await svc.FileVatAsync(i, Me, ct) });

    private FileContentResult Csv(string csv, string name) => File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", name);
}
