using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Products;
using Inventory.Application.Queries;
using Inventory.Application.Stock;
using Inventory.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

public abstract class AppController(ICurrentUser current) : ControllerBase
{
    protected CurrentUser Me => current.User ?? throw new UnauthorizedAccessException();
    /// <summary>CEO or manager (sees cost and profit).</summary>
    /// <summary>Cost prices and inactive products: for roles that may see costs.</summary>
    protected bool IsAdmin => User.HasClaim(AppClaims.Permission, Permissions.CostView);
}

[ApiController, Route("api/products")]
public class ProductsController(ICurrentUser cu, CatalogQueries q, ProductService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Perm.ProductsView)]
    public Task<PagedResult<ProductDto>> List([FromQuery] PageRequest paging, [FromQuery] bool includeInactive, CancellationToken ct) =>
        q.ProductsAsync(paging, includeInactive && IsAdmin, IsAdmin, ct);

    [HttpGet("{id:int}"), Authorize(Policy = Perm.ProductsView)]
    public async Task<ActionResult<ProductDto>> Get(int id, CancellationToken ct) =>
        await q.ProductAsync(id, IsAdmin, ct) is { } p ? p : NotFound();

    /// <summary>The POS scan field: plain product barcode (our own QR tags are handled by /api/sales/by-number).</summary>
    [HttpGet("by-barcode/{code}"), Authorize(Policy = Perm.ProductsView)]
    public async Task<ActionResult<ProductDto>> ByBarcode(string code, CancellationToken ct) =>
        await q.ProductByBarcodeAsync(code, IsAdmin, ct) is { } p ? p : NotFound();

    [HttpPost, Authorize(Policy = Perm.ProductsCreate)]   // managers and the CEO add products; clerks don't add records
    public async Task<IActionResult> Create(NewProductRequest req, CancellationToken ct)
    {
        var id = await svc.CreateAsync(req, Me, ct);
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPut("{id:int}"), Authorize(Policy = Perm.ProductsEdit)]
    public async Task<IActionResult> Update(int id, ProductInput input, CancellationToken ct)
    {
        await svc.UpdateAsync(id, input, Me, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}"), Authorize(Policy = Perm.ProductsDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await DeleteAsync(id, ct);

    /// <summary>The suppliers this product is bought from (for the product form's "Supplied by" picker).</summary>
    [HttpGet("{id:int}/suppliers"), Authorize(Policy = Perm.SuppliersManage)]
    public Task<List<ProductSupplierDto>> Suppliers(int id, CancellationToken ct) => svc.SuppliersAsync(id, ct);

    /// <summary>The product's photo for the customer catalog. PNG or JPEG, up to 2 MB.</summary>
    [HttpPut("{id:int}/image"), Authorize(Policy = Perm.ProductsEdit)]
    [RequestSizeLimit(Inventory.Application.Company.CompanyProfileService.MaxImageBytes + 4096)]
    public async Task<IActionResult> SetImage(int id, IFormFile file, CancellationToken ct)
    {
        if (file is null) throw new BusinessRuleException("Choose an image file.");
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        await svc.SetImageAsync(id, ms.ToArray(), Me, ct);
        return NoContent();
    }

    [HttpGet("{id:int}/image"), Authorize(Policy = Perm.ProductsView)]
    public async Task<IActionResult> Image(int id, CancellationToken ct)
    {
        var img = await svc.ImageAsync(id, ct);
        if (img is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(img.Data, img.ContentType);
    }

    [HttpDelete("{id:int}/image"), Authorize(Policy = Perm.ProductsDelete)]
    public async Task<IActionResult> RemoveImage(int id, CancellationToken ct) { await svc.RemoveImageAsync(id, Me, ct); return NoContent(); }

    private async Task<IActionResult> DeleteAsync(int id, CancellationToken ct)
    {
        var hard = await svc.DeleteAsync(id, Me, ct);
        return Ok(new { deleted = hard, deactivated = !hard });
    }
}

public sealed record NameRequest(string Name, string? Location);

[ApiController, Route("api")]
public class ReferenceDataController(ICurrentUser cu, CatalogQueries q, PartnerService svc) : AppController(cu)
{
    [HttpGet("categories"), Authorize(Policy = Perm.ProductsView)]
    public Task<List<CategoryDto>> Categories(CancellationToken ct) => q.CategoriesAsync(ct);

    [HttpPost("categories"), Authorize(Policy = Perm.CatalogSetup)]
    public async Task<IActionResult> AddCategory(NameRequest r, CancellationToken ct) => Ok(new { id = await svc.CreateCategoryAsync(r.Name, Me, ct) });

    [HttpGet("warehouses"), Authorize(Policy = Perm.StockView)]
    public Task<List<WarehouseDto>> Warehouses(CancellationToken ct) => q.WarehousesAsync(ct);

    [HttpPost("warehouses"), Authorize(Policy = Perm.CatalogSetup)]
    public async Task<IActionResult> AddWarehouse(NameRequest r, CancellationToken ct) => Ok(new { id = await svc.CreateWarehouseAsync(r.Name, r.Location, Me, ct) });
}

[ApiController, Route("api/inventory")]
public class InventoryController(ICurrentUser cu, CatalogQueries q, Inventory.Application.Stock.StockConsolidationService consolidation) : AppController(cu)
{
    /// <summary>What "Tidy up" would do: products sharing a name that would become one, and stock rows that would be added together. Changes nothing.</summary>
    [HttpGet("consolidate/preview"), Authorize(Policy = Perm.StockAdjust)]
    public Task<Inventory.Application.Stock.ConsolidationPreview> ConsolidatePreview(CancellationToken ct) => consolidation.PreviewAsync(ct);

    /// <summary>Does it. Quantities are added, never lost (the total on hand must be identical afterwards or nothing is saved).</summary>
    [HttpPost("consolidate"), Authorize(Policy = Perm.StockAdjust)]
    public async Task<object> Consolidate(CancellationToken ct)
    {
        var result = await consolidation.RunAsync(Me, ct);
        var guard = await consolidation.EnsureUniqueNameGuardAsync(ct);
        return new { result.ProductsMerged, result.StockRowsMerged, result.StockOnHandBefore, result.StockOnHandAfter, DatabaseRefusesDuplicateNames = guard };
    }

    [HttpGet("batches"), Authorize(Policy = Perm.StockView)]
    public Task<PagedResult<BatchRowDto>> Batches([FromQuery] PageRequest paging, [FromQuery] bool onlyLow, CancellationToken ct) => q.BatchesAsync(paging, onlyLow, ct);

    /// <param name="referenceType">Production | Transfer | PurchaseOrder | Invoice | OpeningBalance | Manual …</param>
    [HttpGet("movements"), Authorize(Policy = Perm.StockView)]
    public Task<PagedResult<MovementRowDto>> Movements([FromQuery] PageRequest paging, [FromQuery] string? referenceType, [FromQuery] int? productId, CancellationToken ct) =>
        q.MovementsAsync(paging, referenceType, productId, ct);
}

public sealed record ProductionRequest(int ProductId, int WarehouseId, int Quantity, DateOnly? ProducedOn, string? BatchNumber, DateOnly? ExpiryDate);
public sealed record CorrectProductionRequest(int Quantity, DateOnly ProducedOn);
public sealed record TransferRequest(int ProductId, int FromWarehouseId, int ToWarehouseId, int Quantity);
public sealed record AdjustRequest(int BatchId, int Delta, string Reason);

[ApiController, Route("api/stock")]
public class StockController(ICurrentUser cu, StockOperations ops, IClock clock) : AppController(cu)
{
    [HttpPost("production"), Authorize(Policy = Perm.StockReceive)]
    public async Task<IActionResult> Produce(ProductionRequest r, CancellationToken ct) =>
        Ok(new { movementId = await ops.RecordProductionAsync(r.ProductId, r.WarehouseId, r.Quantity, r.ProducedOn ?? clock.BusinessToday, r.BatchNumber, r.ExpiryDate, Me, ct) });

    [HttpPut("production/{movementId:int}"), Authorize(Policy = Perm.StockCorrect)]
    public async Task<IActionResult> Correct(int movementId, CorrectProductionRequest r, CancellationToken ct)
    {
        await ops.CorrectProductionAsync(movementId, r.Quantity, r.ProducedOn, Me, ct);
        return NoContent();
    }

    /// <summary>"Delete" a production entry: reverses its stock and keeps the history row with quantity 0 (defect D6).</summary>
    [HttpDelete("production/{movementId:int}"), Authorize(Policy = Perm.StockDelete)]
    public async Task<IActionResult> DeleteProduction(int movementId, CancellationToken ct)
    {
        await ops.CorrectProductionAsync(movementId, 0, clock.BusinessToday, Me, ct);
        return NoContent();
    }

    [HttpPost("transfers"), Authorize(Policy = Perm.StockTransfer)]
    public async Task<IActionResult> Transfer(TransferRequest r, CancellationToken ct)
    {
        await ops.TransferAsync(r.ProductId, r.FromWarehouseId, r.ToWarehouseId, r.Quantity, Me, ct);
        return NoContent();
    }

    [HttpPost("adjustments"), Authorize(Policy = Perm.StockAdjust)]
    public async Task<IActionResult> Adjust(AdjustRequest r, [FromServices] Inventory.Application.Approvals.ApprovalService approvals, CancellationToken ct)
    {
        // Above the business's limit (off until switched on under Approvals) the adjustment is held for someone else's approval (202) instead of being made.
        var value = await approvals.AdjustmentValueAsync(r.BatchId, r.Delta, ct);
        if (await approvals.NeedsApprovalAsync(Inventory.Domain.Entities.ApprovalActions.StockAdjust, value, ct))
            return Accepted(await approvals.SubmitAsync(Inventory.Domain.Entities.ApprovalActions.StockAdjust, value,
                $"Adjust batch #{r.BatchId} by {r.Delta:+#;-#}: {r.Reason}", new Inventory.Application.Approvals.AdjustPayload(r.BatchId, r.Delta, r.Reason), Me, ct));
        await ops.AdjustAsync(r.BatchId, r.Delta, r.Reason, Me, ct);
        return NoContent();
    }
}
