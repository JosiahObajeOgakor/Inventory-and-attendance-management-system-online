using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Products;
using Inventory.Application.Purchasing;
using Inventory.Application.Queries;
using Inventory.Application.Sales;
using Inventory.Application.Stock;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>
/// One product = one name and one stock row per warehouse and expiry date. Buying more tops the row up instead of adding another; "low stock" is about the product, not an old
/// emptied row; the Stock list pages without repeating or skipping rows; and what was already duplicated can be merged without losing a unit.
/// </summary>
[Collection("mysql")]
public class StockTidyTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private static readonly ICompanyContext Co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd"));

    private sealed record Rig(string Cs, Seed Seed, PurchaseService Purchases, ProductService Products, StockConsolidationService Tidy, int SupplierId);

    private async Task<Rig> BuildAsync(int stock = 10)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock);
        var db = NewContext(cs);
        var tx = Wire.Tx(db);
        var stockSvc = new StockService(db, Clock);
        var supplier = new Supplier { Name = "Agro Mills Ltd" };
        db.Suppliers.Add(supplier); await db.SaveChangesAsync();
        return new Rig(cs, seed, new PurchaseService(db, tx, stockSvc, Clock, Co, new PurchaseRequestValidator()), new ProductService(db, tx, stockSvc, Clock, Co),
            new StockConsolidationService(db, tx, Clock), supplier.Id);
    }

    private static Task<PurchaseResult> Buy(Rig r, int qty, int? productId = null) => r.Purchases.SaveAsync(new PurchaseRequest
    {
        SupplierId = r.SupplierId, VatRate = 0, ReceiveNow = true, WarehouseId = r.Seed.WarehouseId,
        Lines = [new PurchaseLineDto { ProductId = productId ?? r.Seed.ProductId, Quantity = qty, UnitCost = 8500 }],
    }, Clerk, null);

    private static CatalogQueries Catalog(BusinessDbContext db) => new(db, new NoUsers(), Clock);

    [Fact]
    public async Task Buying_more_tops_up_the_existing_stock_row_instead_of_adding_another()
    {
        var r = await BuildAsync(stock: 10);
        var first = await Buy(r, 25);
        var second = await Buy(r, 15);

        await using var db = NewContext(r.Cs);
        var rows = await db.StockBatches.AsNoTracking().Where(b => b.ProductId == r.Seed.ProductId).ToListAsync();
        var row = Assert.Single(rows);                                        // still ONE row for the product in that warehouse
        Assert.Equal(50, row.QuantityOnHand);                                 // 10 + 25 + 15
        var ins = await db.StockMovements.AsNoTracking().Where(m => m.ProductId == r.Seed.ProductId && m.MovementType == "IN").ToListAsync();
        Assert.Equal(2, ins.Count(m => m.ReferenceType == "PurchaseOrder"));  // the history still shows each purchase …
        Assert.Contains(ins, m => m.ReferenceId == first.PurchaseOrderId && m.Quantity == 25);
        Assert.Contains(ins, m => m.ReferenceId == second.PurchaseOrderId && m.Note!.Contains(second.PoNumber));   // … and which order it was
    }

    [Fact]
    public async Task Adding_stock_with_a_batch_number_typed_still_tops_up_the_existing_row()
    {
        var r = await BuildAsync(stock: 10);
        await using (var db = NewContext(r.Cs))
        {
            var ops = new StockOperations(db, Wire.Tx(db), new StockService(db, Clock), Clock);
            await ops.RecordProductionAsync(r.Seed.ProductId, r.Seed.WarehouseId, 7, DateOnly.FromDateTime(DateTime.UtcNow), "BATCH-OCT-21", null, Clerk);
            await ops.RecordProductionAsync(r.Seed.ProductId, r.Seed.WarehouseId, 5, DateOnly.FromDateTime(DateTime.UtcNow), "BATCH-OCT-22", null, Clerk);
        }
        await using var check = NewContext(r.Cs);
        var row = Assert.Single(await check.StockBatches.AsNoTracking().Where(b => b.ProductId == r.Seed.ProductId).ToListAsync());   // still one row
        Assert.Equal(22, row.QuantityOnHand);                                                                                           // 10 + 7 + 5
        Assert.Equal(2, await check.StockMovements.CountAsync(m => m.ProductId == r.Seed.ProductId && m.ReferenceType == "Production"));   // both additions are in the history
    }

    [Fact]
    public async Task A_transfer_onto_a_warehouse_that_already_holds_the_product_tops_up_its_row()
    {
        var r = await BuildAsync(stock: 30);
        int other;
        await using (var db = NewContext(r.Cs))
        {
            var w = new Warehouse { Name = "Second store" };
            db.Warehouses.Add(w); await db.SaveChangesAsync();
            other = w.Id;
            db.StockBatches.Add(new StockBatch { ProductId = r.Seed.ProductId, WarehouseId = other, BatchNumber = "THEIRS", QuantityOnHand = 4 });
            await db.SaveChangesAsync();
            await new StockOperations(db, Wire.Tx(db), new StockService(db, Clock), Clock).TransferAsync(r.Seed.ProductId, r.Seed.WarehouseId, other, 9, Clerk);
        }
        await using var check = NewContext(r.Cs);
        var there = Assert.Single(await check.StockBatches.AsNoTracking().Where(b => b.ProductId == r.Seed.ProductId && b.WarehouseId == other).ToListAsync());
        Assert.Equal(13, there.QuantityOnHand);
        Assert.Equal(21, (await check.StockBatches.AsNoTracking().SingleAsync(b => b.ProductId == r.Seed.ProductId && b.WarehouseId == r.Seed.WarehouseId)).QuantityOnHand);
    }

    [Fact]
    public async Task Restocking_tops_up_the_one_row_whatever_the_expiry_date_and_keeps_the_earliest_date()
    {
        var r = await BuildAsync(stock: 0);
        await using (var db = NewContext(r.Cs))
        {
            var stockSvc = new StockService(db, Clock);
            var tx = Wire.Tx(db);
            await tx.RunAsync<bool>(async ct =>
            {
                var d1 = new DateOnly(2027, 3, 1); var d2 = new DateOnly(2027, 9, 1);
                await stockSvc.ReceiveToShelfAsync(r.Seed.ProductId, r.Seed.WarehouseId, 5, d1, "Manual", null, 1, ct);
                await stockSvc.ReceiveToShelfAsync(r.Seed.ProductId, r.Seed.WarehouseId, 7, d1, "Manual", null, 1, ct);     // same transaction: still one row
                await stockSvc.ReceiveToShelfAsync(r.Seed.ProductId, r.Seed.WarehouseId, 4, d2, "Manual", null, 1, ct);
                await db.SaveChangesAsync(ct); return true;
            }, default);
        }
        await using var check = NewContext(r.Cs);
        var rows = await check.StockBatches.AsNoTracking().Where(b => b.ProductId == r.Seed.ProductId && b.QuantityOnHand > 0).OrderBy(b => b.ExpiryDate).ToListAsync();
        var row = Assert.Single(rows);                                          // one row for the product here, not one per expiry date
        Assert.Equal(16, row.QuantityOnHand);
        Assert.Equal(new DateOnly(2027, 3, 1), row.ExpiryDate);                 // the earliest date is kept
    }

    [Fact]
    public async Task Low_stock_is_judged_for_the_product_so_an_old_emptied_row_is_not_flagged_and_is_hidden()
    {
        var r = await BuildAsync(stock: 0);                                   // the seeded row is now empty history
        await Buy(r, 40);                                                     // reorder level is 5: 40 on hand is plenty
        await using var db = NewContext(r.Cs);
        db.StockBatches.Add(new StockBatch { ProductId = r.Seed.ProductId, WarehouseId = r.Seed.WarehouseId, BatchNumber = "OLD-1", QuantityOnHand = 0 });
        await db.SaveChangesAsync();

        var page = await Catalog(db).BatchesAsync(new PageRequest(1, 50), onlyLow: false, default);
        var row = Assert.Single(page.Items);                                  // only the row that holds stock
        Assert.Equal(("OK", 40), (row.Status, row.Quantity));
        Assert.Empty((await Catalog(db).BatchesAsync(new PageRequest(1, 50), onlyLow: true, default)).Items);   // and it is not "low"

        await db.StockBatches.Where(b => b.QuantityOnHand > 0).ExecuteUpdateAsync(u => u.SetProperty(b => b.QuantityOnHand, 3));
        var low = Assert.Single((await Catalog(db).BatchesAsync(new PageRequest(1, 50), onlyLow: true, default)).Items);
        Assert.Equal("Low stock", low.Status);                                // 3 ≤ 5: now it really is low

        await db.StockBatches.ExecuteUpdateAsync(u => u.SetProperty(b => b.QuantityOnHand, 0));
        var out1 = Assert.Single((await Catalog(db).BatchesAsync(new PageRequest(1, 50), onlyLow: false, default)).Items);
        Assert.Equal("Out of stock", out1.Status);                            // a product that is completely out still shows, once, so you know to buy it
    }

    [Fact]
    public async Task Paging_through_many_rows_with_the_same_name_shows_every_row_exactly_once()
    {
        var r = await BuildAsync(stock: 1);
        await using (var db = NewContext(r.Cs))
        {
            for (var i = 2; i <= 52; i++) db.StockBatches.Add(new StockBatch { ProductId = r.Seed.ProductId, WarehouseId = r.Seed.WarehouseId, BatchNumber = $"B{i}", QuantityOnHand = i });
            await db.SaveChangesAsync();                                      // 52 rows, identical category, name and expiry: the old order had nothing to separate them
        }
        await using var check = NewContext(r.Cs);
        var seen = new List<int>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await Catalog(check).BatchesAsync(new PageRequest(page, 15), false, default);
            Assert.Equal(52, result.Total); Assert.Equal(page, result.Page);
            seen.AddRange(result.Items.Select(i => i.BatchId));
        }
        Assert.Equal(52, seen.Count);
        Assert.Equal(52, seen.Distinct().Count());                            // no repeats and no gaps between pages
    }

    [Fact]
    public async Task A_second_product_with_the_same_name_is_refused_however_it_is_typed()
    {
        var r = await BuildAsync();
        ProductInput Input(string sku, string name) => new() { Sku = sku, Name = name, CategoryId = 1, Unit = "Bag", PriceRetail = 100 };

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => r.Products.CreateAsync(new NewProductRequest { Product = Input("NEW-1", "  adult   DOG food 20KG ") }, Clerk));
        Assert.Contains("There is already a product named \"Adult Dog Food 20kg\"", ex.Message);
        Assert.Contains("Record a purchase", ex.Message);

        var other = await r.Products.CreateAsync(new NewProductRequest { Product = Input("NEW-2", "Kitten Food 2kg") }, Clerk);
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Products.UpdateAsync(other, Input("NEW-2", "ADULT DOG FOOD 20KG"), Clerk));   // renaming onto an existing name
        await r.Products.UpdateAsync(other, Input("NEW-2", "Kitten Food 2kg"), Clerk);                                                          // keeping its own name is fine
    }

    [Fact]
    public async Task Tidy_up_merges_duplicate_products_and_rows_without_losing_a_unit_or_any_history()
    {
        var r = await BuildAsync(stock: 10);
        int dupId;
        await using (var db = NewContext(r.Cs))
        {
            // The mess: a second product with the same name (typed with different case/spacing), its own stock, and extra rows for the first.
            var dup = new Product { Sku = "DUP-1", Name = "adult dog food  20KG", CategoryId = 1, CostPrice = 8500, PriceRetail = 11500, PriceWholesaler = 11000, PriceDistributor = 10500, SellingPrice = 11500, ReorderLevel = 5 };
            db.Products.Add(dup); await db.SaveChangesAsync(); dupId = dup.Id;
            db.StockBatches.AddRange(
                new StockBatch { ProductId = r.Seed.ProductId, WarehouseId = r.Seed.WarehouseId, BatchNumber = "PO-1", QuantityOnHand = 20 },
                new StockBatch { ProductId = r.Seed.ProductId, WarehouseId = r.Seed.WarehouseId, BatchNumber = "PO-2", QuantityOnHand = 0 },
                new StockBatch { ProductId = dupId, WarehouseId = r.Seed.WarehouseId, BatchNumber = "B1", QuantityOnHand = 7 },
                new StockBatch { ProductId = dupId, WarehouseId = r.Seed.WarehouseId, BatchNumber = "EXP", QuantityOnHand = 3, ExpiryDate = new DateOnly(2027, 5, 1) });
            await db.SaveChangesAsync();
        }
        // History that must survive: a sale of the duplicate product, and a purchase order line for it.
        await using (var db = NewContext(r.Cs))
        {
            var sale = Sale(new Seed(dupId, r.Seed.WarehouseId, r.Seed.CustomerId), 2, vat: 0, paid: 23000);
            sale.PaymentMethod = "Cash";
            await SalesFor(db).SaveAsync(sale, Clerk, null);
        }
        await Buy(r, 5, dupId);

        int totalBefore;
        await using (var db = NewContext(r.Cs)) totalBefore = await db.StockBatches.SumAsync(b => b.QuantityOnHand);

        var preview = await r.Tidy.PreviewAsync(default);
        Assert.False(preview.NothingToDo);
        Assert.Equal(1, preview.ProductsToMerge);

        var result = await r.Tidy.RunAsync(Clerk, default);
        Assert.Equal(1, result.ProductsMerged);
        Assert.Equal(totalBefore, result.StockOnHandAfter);

        await using var check = NewContext(r.Cs);
        var product = Assert.Single(await check.Products.AsNoTracking().ToListAsync());                  // one product left: the original
        Assert.Equal(r.Seed.ProductId, product.Id);
        Assert.Equal(totalBefore, await check.StockBatches.SumAsync(b => b.QuantityOnHand));              // not one unit lost or gained
        var rows = await check.StockBatches.AsNoTracking().OrderBy(b => b.ExpiryDate).ToListAsync();
        Assert.Equal(2, rows.Count);                                                                       // one row per expiry date: ordinary stock + the dated stock
        Assert.Equal(1, rows.Single(b => b.ExpiryDate != null).QuantityOnHand);                            // 3 dated units, 2 of them sold earliest-expiry-first
        Assert.Equal(10 + 20 + 7 + 5, rows.Single(b => b.ExpiryDate == null).QuantityOnHand);              // original 10 + PO-1 20 + the duplicate's 7 + 5 bought for it, in ONE row
        Assert.All(rows, b => Assert.Equal(r.Seed.ProductId, b.ProductId));
        Assert.Empty(await check.InvoiceItems.AsNoTracking().Where(i => i.ProductId == dupId).ToListAsync());                // every sale line now belongs to the survivor
        Assert.Equal(2, await check.InvoiceItems.AsNoTracking().Where(i => i.ProductId == r.Seed.ProductId).SumAsync(i => i.Quantity));
        Assert.Empty(await check.PurchaseOrderItems.AsNoTracking().Where(i => i.ProductId == dupId).ToListAsync());
        Assert.Empty(await check.StockMovements.AsNoTracking().Where(m => m.ProductId == dupId).ToListAsync());
        Assert.Contains(await check.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "PRODUCT_MERGED");

        Assert.True(await r.Tidy.EnsureUniqueNameGuardAsync(default));                                     // and the database now refuses a new duplicate by itself
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var d = NewContext(r.Cs);
            d.Products.Add(new Product { Sku = "DUP-2", Name = "ADULT DOG FOOD 20KG", CategoryId = 1 });
            await d.SaveChangesAsync();
        });

        Assert.True((await r.Tidy.PreviewAsync(default)).NothingToDo);                                      // and a second tidy-up has nothing to do
    }
}
