using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Documents;
using Inventory.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

[ApiController, Route("api/company")]
public class CompanyController(ICurrentUser cu, CompanyProfileService svc) : AppController(cu)
{
    /// <summary>Clerks see the profile too (their documents carry it); only admins change it.</summary>
    [HttpGet("profile"), Authorize(Policy = Policies.Staff)]
    public Task<CompanyProfileDto> Get(CancellationToken ct) => svc.GetAsync(ct);

    [HttpPut("profile"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(CompanyProfileInput input, CancellationToken ct)
    {
        await svc.UpdateAsync(input, Me, ct);
        return NoContent();
    }

    /// <summary>Logo, receipt stamp/signature, waybill stamp. PNG or JPEG, up to 2 MB; the bytes are inspected, not just the file name.</summary>
    [HttpPut("assets/{kind}"), Authorize(Policy = Policies.Admin)]
    [RequestSizeLimit(CompanyProfileService.MaxImageBytes + 4096)]
    public async Task<IActionResult> SetAsset(string kind, IFormFile file, CancellationToken ct)
    {
        if (file is null) throw new BusinessRuleException("Choose an image file.");
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        await svc.SetAssetAsync(kind, ms.ToArray(), Me, ct);
        return NoContent();
    }

    [HttpGet("assets/{kind}"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> GetAsset(string kind, CancellationToken ct)
    {
        if (!AssetKinds.All.Contains(kind)) return NotFound();
        var a = await svc.GetAssetAsync(kind, ct);
        if (a is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(a.Data, a.ContentType);
    }

    [HttpDelete("assets/{kind}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> RemoveAsset(string kind, CancellationToken ct)
    {
        if (!AssetKinds.All.Contains(kind)) return NotFound();
        await svc.RemoveAssetAsync(kind, ct);
        return NoContent();
    }
}

/// <summary>Printable documents, rendered on the server so every device prints the same thing. Inline PDFs open in the browser's viewer.</summary>
[ApiController, Route("api")]
public class DocumentsController(ICurrentUser cu, DocumentQueries docs, IDocumentRenderer renderer, IBusinessDbContext db, ICompanyContext company) : AppController(cu)
{
    private FileContentResult Pdf(byte[] bytes, string name)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = $"inline; filename=\"{name}.pdf\"";
        return File(bytes, "application/pdf");
    }

    [HttpGet("sales/{id:int}/receipt.pdf"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Receipt(int id, CancellationToken ct)
    {
        var d = await docs.ReceiptAsync(id, ct);
        return Pdf(renderer.Receipt(d), "Receipt-" + d.Number);
    }

    [HttpGet("quotations/{id:int}/pdf"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Quotation(int id, CancellationToken ct)
    {
        var d = await docs.QuotationAsync(id, ct);
        return Pdf(renderer.Quotation(d), "Quotation-" + d.Number);
    }

    [HttpGet("waybills/{id:int}/pdf"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Waybill(int id, CancellationToken ct)
    {
        var d = await docs.WaybillAsync(id, ct);
        return Pdf(renderer.Waybill(d), "Waybill-" + d.Number);
    }

    /// <summary>Candid Purrfect keeps a price list; other businesses don't (as in the desktop app).</summary>
    [HttpGet("price-list.pdf"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> PriceList([FromQuery] string? customer, [FromQuery] bool includeOutOfStock, CancellationToken ct, [FromQuery] string tier = "Retailer")
    {
        if (!company.HasPriceLists) return NotFound();
        var d = await docs.PriceListAsync(tier, customer, includeOutOfStock, ct);
        return Pdf(renderer.PriceList(d), "PriceList-" + d.Reference);
    }

    [HttpGet("products/{id:int}/label.pdf"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> ShelfLabel(int id, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Product");
        if (string.IsNullOrEmpty(p.Barcode)) throw new BusinessRuleException("This product has no barcode yet.");
        var brand = await docs.BrandAsync(ct);
        return Pdf(renderer.ShelfLabel(brand, p.Name, p.Sku, p.Barcode, p.PriceRetail), "Label-" + p.Sku);
    }
}
