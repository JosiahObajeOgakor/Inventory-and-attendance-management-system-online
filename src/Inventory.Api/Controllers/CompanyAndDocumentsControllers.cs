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

/// <summary>A catalog to send: what to build, and where to (an email address, or a WhatsApp number; blank = the customer's own).</summary>
public sealed record CatalogSendRequest(CatalogRequest Request, string? To, string? Note);

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

    [HttpDelete("assets/{kind}"), Authorize(Policy = Policies.Ceo)]
    public async Task<IActionResult> RemoveAsset(string kind, CancellationToken ct)
    {
        if (!AssetKinds.All.Contains(kind)) return NotFound();
        await svc.RemoveAssetAsync(kind, ct);
        return NoContent();
    }
}

/// <summary>Where we deliver and what it costs. Staff can see the list (to quote delivery); only admins change it.</summary>
[ApiController, Route("api/delivery-zones")]
public class DeliveryZonesController(ICurrentUser cu, DeliveryZoneService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<IReadOnlyList<DeliveryZoneDto>> List(CancellationToken ct) => svc.ListAsync(activeOnly: false, ct);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Create(DeliveryZoneInput input, CancellationToken ct) => Ok(new { id = await svc.CreateAsync(input, Me, ct) });

    [HttpPut("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(int id, DeliveryZoneInput input, CancellationToken ct)
    {
        await svc.UpdateAsync(id, input, Me, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}"), Authorize(Policy = Policies.Ceo)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await svc.DeleteAsync(id, Me, ct);
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

    /// <summary>A purchase from a supplier's own items — their copy, with no stock or product data on it.</summary>
    [HttpGet("supplies/{id:int}/pdf"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Supply(int id, CancellationToken ct)
    {
        var d = await docs.SupplyAsync(id, ct);
        return Pdf(renderer.Supply(d), "Purchase-" + d.Number);
    }

    [HttpGet("stock-purchases/{id:int}/pdf"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> PurchaseOrder(int id, CancellationToken ct)
    {
        var d = await docs.PurchaseOrderAsync(id, ct);
        return Pdf(renderer.PurchaseOrder(d), "Purchase-order-" + d.Number);
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

    /// <summary>
    /// The price list / catalog for chosen items (or all), in a tier, as a PDF or one PNG image — for any business. Opens inline so it can be
    /// printed, saved or shared from the phone.
    /// </summary>
    [HttpPost("catalog/file"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> CatalogFile(CatalogRequest r, [FromServices] CatalogService catalog, CancellationToken ct)
    {
        var f = await catalog.FileAsync(r, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = $"inline; filename=\"{f.FileName}\"";
        return File(f.Bytes, f.ContentType);
    }

    [HttpPost("catalog/email"), Authorize(Policy = Policies.Staff)]
    public Task<CatalogSent> CatalogEmail(CatalogSendRequest r, [FromServices] CatalogService catalog, CancellationToken ct) =>
        catalog.EmailAsync(r.Request, r.To, r.Note, Me, ct);

    [HttpPost("catalog/whatsapp"), Authorize(Policy = Policies.Staff)]
    public Task<CatalogSent> CatalogWhatsApp(CatalogSendRequest r, [FromServices] CatalogService catalog, CancellationToken ct) =>
        catalog.WhatsAppAsync(r.Request, r.To, Me, ct);

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
