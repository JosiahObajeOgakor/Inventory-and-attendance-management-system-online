using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Products;

public sealed class ProductInput
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public string Unit { get; set; } = "Bag";
    public int ReorderLevel { get; set; }
    public decimal CostPrice { get; set; }
    public decimal PriceDistributor { get; set; }
    public decimal PriceWholesaler { get; set; }
    public decimal PriceRetail { get; set; }
    public string? Barcode { get; set; }
    public bool TracksSerial { get; set; }
}

public sealed class NewProductRequest
{
    public ProductInput Product { get; set; } = new();
    /// <summary>Opening stock is optional; when given it is logged as an OPENING_BALANCE movement (defect D4).</summary>
    public int OpeningQuantity { get; set; }
    public int WarehouseId { get; set; }
    public string? BatchNumber { get; set; }
    public DateOnly? ExpiryDate { get; set; }
}

public sealed class ProductInputValidator : AbstractValidator<ProductInput>
{
    public ProductInputValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.Unit).NotEmpty().MaximumLength(20);
        RuleFor(x => x.ReorderLevel).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CostPrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceDistributor).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceWholesaler).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceRetail).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Barcode).MaximumLength(64);
    }
}

public sealed class ProductService(IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock, ICompanyContext company)
{
    public Task<int> CreateAsync(NewProductRequest req, CurrentUser user, CancellationToken ct = default)
    {
        new ProductInputValidator().ValidateAndThrow(req.Product);
        if (req.OpeningQuantity < 0) throw new BusinessRuleException("Opening quantity cannot be negative.");
        return tx.RunAsync(async inner =>
        {
            var p = req.Product;
            if (await db.Products.AnyAsync(x => x.Sku == p.Sku, inner)) throw new BusinessRuleException($"A product with SKU \"{p.Sku}\" already exists.");
            var barcode = string.IsNullOrWhiteSpace(p.Barcode) ? null : p.Barcode.Trim();
            if (barcode is not null && await db.Products.AnyAsync(x => x.Barcode == barcode, inner))
                throw new BusinessRuleException("That barcode already belongs to another product.");
            if (!await db.Categories.AnyAsync(c => c.Id == p.CategoryId, inner)) throw new NotFoundException("Category");

            var product = new Product
            {
                Sku = p.Sku.Trim(), Name = p.Name.Trim(), CategoryId = p.CategoryId, Unit = p.Unit.Trim(), ReorderLevel = p.ReorderLevel,
                CostPrice = p.CostPrice, PriceDistributor = p.PriceDistributor, PriceWholesaler = p.PriceWholesaler,
                PriceRetail = p.PriceRetail, SellingPrice = p.PriceRetail, Barcode = barcode, TracksSerial = p.TracksSerial,
            };
            db.Products.Add(product);
            await db.SaveChangesAsync(inner);
            // A scanned supplier code is kept; an unlabelled product gets an in-store one, which needs the new id.
            product.Barcode ??= Barcodes.MintInternalBarcode(product.Id);

            if (req.OpeningQuantity > 0)
            {
                var warehouseId = req.WarehouseId > 0 ? req.WarehouseId : await db.Warehouses.MinAsync(w => w.Id, inner);
                if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, inner)) throw new NotFoundException("Warehouse");
                await stock.ReceiveAsync(product.Id, warehouseId, string.IsNullOrWhiteSpace(req.BatchNumber) ? "OPENING" : req.BatchNumber.Trim(),
                    req.OpeningQuantity, req.ExpiryDate, MovementReferences.OpeningBalance, null, user.Id, inner);
            }
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PRODUCT_CREATED", Entity = "Product", EntityId = product.Id.ToString(), At = clock.UtcNow, Detail = product.Sku });
            await db.SaveChangesAsync(inner);
            return product.Id;
        }, ct);
    }

    /// <summary>Edit cost, tier prices, reorder level (Admin). SellingPrice stays equal to the retail price.</summary>
    public async Task UpdateAsync(int id, ProductInput input, CurrentUser user, CancellationToken ct = default)
    {
        await new ProductInputValidator().ValidateAndThrowAsync(input, ct);
        var p = await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product");
        if (await db.Products.AnyAsync(x => x.Sku == input.Sku && x.Id != id, ct)) throw new BusinessRuleException($"A product with SKU \"{input.Sku}\" already exists.");
        var barcode = string.IsNullOrWhiteSpace(input.Barcode) ? p.Barcode : input.Barcode.Trim();
        if (barcode is not null && await db.Products.AnyAsync(x => x.Barcode == barcode && x.Id != id, ct))
            throw new BusinessRuleException("That barcode already belongs to another product.");

        if (company.HasPriceLists && (p.PriceDistributor != input.PriceDistributor || p.PriceWholesaler != input.PriceWholesaler || p.PriceRetail != input.PriceRetail))
            db.PriceChanges.Add(new PriceChange { ProductId = id, OldDistributor = p.PriceDistributor, OldWholesaler = p.PriceWholesaler, OldRetail = p.PriceRetail,
                NewDistributor = input.PriceDistributor, NewWholesaler = input.PriceWholesaler, NewRetail = input.PriceRetail, ChangedAt = clock.UtcNow, ChangedByUserId = user.Id, Note = "Edited on product" });
        p.Sku = input.Sku.Trim(); p.Name = input.Name.Trim(); p.CategoryId = input.CategoryId; p.Unit = input.Unit.Trim();
        p.ReorderLevel = input.ReorderLevel; p.CostPrice = input.CostPrice; p.PriceDistributor = input.PriceDistributor;
        p.PriceWholesaler = input.PriceWholesaler; p.PriceRetail = input.PriceRetail; p.SellingPrice = input.PriceRetail;
        p.Barcode = barcode; p.TracksSerial = input.TracksSerial;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PRODUCT_UPDATED", Entity = "Product", EntityId = id.ToString(), At = clock.UtcNow, Detail = p.Sku });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Delete (Admin). Never removes stock silently (defect D3): a product with any history is deactivated; a
    /// product with none is deleted only if it holds no stock. Returns true when it was hard-deleted.
    /// </summary>
    public Task<bool> DeleteAsync(int id, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var p = await db.Products.SingleOrDefaultAsync(x => x.Id == id, inner) ?? throw new NotFoundException("Product");
            var referenced = await db.InvoiceItems.AnyAsync(i => i.ProductId == id, inner)
                             || await db.PurchaseOrderItems.AnyAsync(i => i.ProductId == id, inner)
                             || await db.StockMovements.AnyAsync(m => m.ProductId == id, inner);
            var onHand = await db.StockBatches.Where(b => b.ProductId == id).SumAsync(b => (int?)b.QuantityOnHand, inner) ?? 0;
            if (onHand > 0) throw new BusinessRuleException($"\"{p.Name}\" still has {onHand:N0} in stock. Adjust or sell the stock first.");

            bool hardDeleted;
            if (referenced) { p.IsActive = false; hardDeleted = false; }
            else
            {
                db.StockBatches.RemoveRange(await db.StockBatches.Where(b => b.ProductId == id).ToListAsync(inner));
                db.Products.Remove(p);
                hardDeleted = true;
            }
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = hardDeleted ? "PRODUCT_DELETED" : "PRODUCT_DEACTIVATED", Entity = "Product", EntityId = id.ToString(), At = clock.UtcNow, Detail = p.Sku });
            await db.SaveChangesAsync(inner);
            return hardDeleted;
        }, ct);
}
