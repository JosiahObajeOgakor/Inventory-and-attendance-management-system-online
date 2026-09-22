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
    protected bool IsAdmin => User.IsInRole(RoleNames.Admin);
}

[ApiController, Route("api/products")]
public class ProductsController(ICurrentUser cu, CatalogQueries q, ProductService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<ProductDto>> List([FromQuery] PageRequest page, [FromQuery] bool includeInactive, CancellationToken ct) =>
        q.ProductsAsync(page, includeInactive && IsAdmin, IsAdmin, ct);

    [HttpGet("{id:int}"), Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<ProductDto>> Get(int id, CancellationToken ct) =>
        await q.ProductAsync(id, IsAdmin, ct) is { } p ? p : NotFound();

    /// <summary>The POS scan field: plain product barcode (our own QR tags are handled by /api/sales/by-number).</summary>
    [HttpGet("by-barcode/{code}"), Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<ProductDto>> ByBarcode(string code, CancellationToken ct) =>
        await q.ProductByBarcodeAsync(code, IsAdmin, ct) is { } p ? p : NotFound();

    [HttpPost, Authorize(Policy = Policies.Staff)]   // clerks can add items in the desktop app
    public async Task<IActionResult> Create(NewProductRequest req, CancellationToken ct)
    {
        var id = await svc.CreateAsync(req, Me, ct);
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPut("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(int id, ProductInput input, CancellationToken ct)
    {
        await svc.UpdateAsync(id, input, Me, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await DeleteAsync(id, ct);

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
    [HttpGet("categories"), Authorize(Policy = Policies.Staff)]
    public Task<List<CategoryDto>> Categories(CancellationToken ct) => q.CategoriesAsync(ct);

    [HttpPost("categories"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> AddCategory(NameRequest r, CancellationToken ct) => Ok(new { id = await svc.CreateCategoryAsync(r.Name, Me, ct) });

    [HttpGet("warehouses"), Authorize(Policy = Policies.Staff)]
    public Task<List<WarehouseDto>> Warehouses(CancellationToken ct) => q.WarehousesAsync(ct);

    [HttpPost("warehouses"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> AddWarehouse(NameRequest r, CancellationToken ct) => Ok(new { id = await svc.CreateWarehouseAsync(r.Name, r.Location, Me, ct) });
}

[ApiController, Route("api/inventory")]
public class InventoryController(ICurrentUser cu, CatalogQueries q) : AppController(cu)
{
    [HttpGet("batches"), Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<BatchRowDto>> Batches([FromQuery] PageRequest page, [FromQuery] bool onlyLow, CancellationToken ct) => q.BatchesAsync(page, onlyLow, ct);

    /// <param name="referenceType">Production | Transfer | PurchaseOrder | Invoice | OpeningBalance | Manual …</param>
    [HttpGet("movements"), Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<MovementRowDto>> Movements([FromQuery] PageRequest page, [FromQuery] string? referenceType, [FromQuery] int? productId, CancellationToken ct) =>
        q.MovementsAsync(page, referenceType, productId, ct);
}

public sealed record ProductionRequest(int ProductId, int WarehouseId, int Quantity, DateOnly? ProducedOn, string? BatchNumber, DateOnly? ExpiryDate);
public sealed record CorrectProductionRequest(int Quantity, DateOnly ProducedOn);
public sealed record TransferRequest(int ProductId, int FromWarehouseId, int ToWarehouseId, int Quantity);
public sealed record AdjustRequest(int BatchId, int Delta, string Reason);

[ApiController, Route("api/stock")]
public class StockController(ICurrentUser cu, StockOperations ops, IClock clock) : AppController(cu)
{
    [HttpPost("production"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Produce(ProductionRequest r, CancellationToken ct) =>
        Ok(new { movementId = await ops.RecordProductionAsync(r.ProductId, r.WarehouseId, r.Quantity, r.ProducedOn ?? clock.BusinessToday, r.BatchNumber, r.ExpiryDate, Me, ct) });

    [HttpPut("production/{movementId:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Correct(int movementId, CorrectProductionRequest r, CancellationToken ct)
    {
        await ops.CorrectProductionAsync(movementId, r.Quantity, r.ProducedOn, Me, ct);
        return NoContent();
    }

    /// <summary>"Delete" a production entry: reverses its stock and keeps the history row with quantity 0 (defect D6).</summary>
    [HttpDelete("production/{movementId:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> DeleteProduction(int movementId, CancellationToken ct)
    {
        await ops.CorrectProductionAsync(movementId, 0, clock.BusinessToday, Me, ct);
        return NoContent();
    }

    [HttpPost("transfers"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Transfer(TransferRequest r, CancellationToken ct)
    {
        await ops.TransferAsync(r.ProductId, r.FromWarehouseId, r.ToWarehouseId, r.Quantity, Me, ct);
        return NoContent();
    }

    [HttpPost("adjustments"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Adjust(AdjustRequest r, CancellationToken ct)
    {
        await ops.AdjustAsync(r.BatchId, r.Delta, r.Reason, Me, ct);
        return NoContent();
    }
}
