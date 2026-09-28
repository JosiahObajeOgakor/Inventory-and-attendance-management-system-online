using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Products;
using Inventory.Application.Sales;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Maintenance;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>Rebates in naira per unit, back-dated sales, deleting a product for good, and the CEO clearing whole areas of history.</summary>
[Collection("mysql")]
public class RecordsAndRolesTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private static readonly CurrentUser Manager = new(3, "Mo Manager", RoleNames.Manager);
    private static ProductService Products(BusinessDbContext db) => new(db, Wire.Tx(db), new StockService(db, Clock), Clock, Wire.Co());

    [Fact]
    public async Task A_rebate_is_the_customers_naira_amount_times_the_units_bought()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using (var db = NewContext(cs)) { (await db.Customers.SingleAsync()).RebatePerUnit = 150m; await db.SaveChangesAsync(); }
        await using (var db = NewContext(cs)) await SalesFor(db).SaveAsync(Sale(seed, 3), Clerk, null);
        await using var check = NewContext(cs);
        var r = await check.RebateEntries.SingleAsync();
        Assert.Equal(450m, r.Amount);
        Assert.Contains("₦150.00 × 3", r.Note);
    }

    [Fact]
    public async Task Only_a_manager_or_the_CEO_can_date_a_sale_in_the_past_and_never_before_2020()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var past = new DateOnly(2021, 3, 15);
        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<ForbiddenActionException>(() => SalesFor(db).SaveAsync(With(Sale(seed, 1), past), Clerk, null));
        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => SalesFor(db).SaveAsync(With(Sale(seed, 1), new DateOnly(2019, 12, 31)), Manager, null));
        SaleResult ok;
        await using (var db = NewContext(cs)) ok = await SalesFor(db).SaveAsync(With(Sale(seed, 1), past), Manager, null);
        await using var check = NewContext(cs);
        Assert.Equal(past, (await check.Invoices.SingleAsync(i => i.Id == ok.InvoiceId)).InvoiceDate);
    }

    private static SaleRequest With(SaleRequest r, DateOnly d) { r.SaleDate = d; return r; }

    [Fact]
    public async Task Deleting_a_product_removes_it_with_its_stock_and_history_but_not_while_a_sale_uses_it()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        int spareId;
        await using (var db = NewContext(cs))
        {
            var spare = new Product { Sku = "SKU-2", Name = "Old Treats", CategoryId = (await db.Categories.FirstAsync()).Id, PriceRetail = 900 };
            db.Products.Add(spare); await db.SaveChangesAsync(); spareId = spare.Id;
            db.StockBatches.Add(new StockBatch { ProductId = spare.Id, WarehouseId = seed.WarehouseId, BatchNumber = "X", QuantityOnHand = 4 });
            db.StockMovements.Add(new StockMovement { ProductId = spare.Id, WarehouseId = seed.WarehouseId, MovementType = MovementTypes.In, Quantity = 4, MovementDate = DateTime.UtcNow, UserId = 1 });
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext(cs)) await SalesFor(db).SaveAsync(Sale(seed, 1), Clerk, null);

        await using (var db = NewContext(cs))
        {
            var e = await Assert.ThrowsAsync<BusinessRuleException>(() => Products(db).DeleteAsync(seed.ProductId, Wire.Admin));
            Assert.Contains("1 sale", e.Message);
        }
        await using (var db = NewContext(cs)) Assert.True(await Products(db).DeleteAsync(spareId, Wire.Admin));
        await using var check = NewContext(cs);
        Assert.False(await check.Products.AnyAsync(p => p.Id == spareId));
        Assert.False(await check.StockBatches.AnyAsync(b => b.ProductId == spareId));
        Assert.False(await check.StockMovements.AnyAsync(m => m.ProductId == spareId));
        Assert.Contains("4 unit(s) in stock removed", (await check.AuditLogs.SingleAsync(a => a.Action == "PRODUCT_DELETED")).Detail);
    }

    [Fact]
    public async Task Clearing_sales_and_expenses_keeps_a_copy_empties_them_and_zeroes_balances_but_leaves_stock()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using (var db = NewContext(cs)) await SalesFor(db).SaveAsync(Sale(seed, 2, vat: 0, paid: 5000), Clerk, null);
        await using (var db = NewContext(cs))
        {
            db.Expenses.Add(new Expense { Category = "Fuel", ExpenseDate = new DateOnly(2024, 1, 2), Amount = 1000, CreatedByUserId = 1 });
            await db.SaveChangesAsync();
        }
        var folder = Path.Combine(Path.GetTempPath(), "inv-clear-" + Guid.NewGuid().ToString("N"));
        ArchiveResult r;
        await using (var db = NewContext(cs))
            r = await new ArchiveService(db, Wire.Tx(db), Clock, Wire.Co(), Options.Create(new ArchiveOptions { Folder = folder }))
                .ClearAsync([HistoryAreas.Sales, HistoryAreas.Expenses], Wire.Admin, default);

        Assert.True(r.Records > 0);
        Assert.True(File.Exists(Path.Combine(folder, "test", r.File)));
        await using var check = NewContext(cs);
        Assert.Empty(await check.Invoices.ToListAsync());
        Assert.Empty(await check.Payments.ToListAsync());
        Assert.Empty(await check.Expenses.ToListAsync());
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
        Assert.Equal(8, await check.StockBatches.SumAsync(b => b.QuantityOnHand));   // stock on hand is untouched
        Assert.Single(await check.AuditLogs.Where(a => a.Action == "HISTORY_CLEARED").ToListAsync());
        try { Directory.Delete(folder, true); } catch { /* temp */ }
    }
}
