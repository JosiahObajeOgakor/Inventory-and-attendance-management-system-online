using Inventory.Application.Analytics;
using Inventory.Application.Sales;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

[Collection("mysql")]
public class AnalyticsServiceTests(MySqlFixture mysql)
{
    /// <summary>Twelve weekly sales of 10 × Dog Food and 5 × Treats, back-dated, so there is a real history to read.</summary>
    private async Task<(string Cs, Seed Seed, int TreatsId)> SeededHistory()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 500);
        int treats;
        await using (var db = NewContext(cs))
        {
            var p = new Product { Sku = "SKU-2", Name = "Dog Treats", CategoryId = 1, CostPrice = 800, PriceRetail = 1200, PriceWholesaler = 1100, PriceDistributor = 1000, SellingPrice = 1200 };
            db.Products.Add(p); await db.SaveChangesAsync(); treats = p.Id;
            db.StockBatches.Add(new StockBatch { ProductId = treats, WarehouseId = seed.WarehouseId, BatchNumber = "T1", QuantityOnHand = 400 });
            await db.SaveChangesAsync();
        }
        for (var w = 12; w >= 1; w--)
        {
            await using var s = NewContext(cs);
            var req = Sale(seed, 10, vat: 0);
            req.SaleDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7 * w + 1);
            req.Lines.Add(new SaleLineDto { ProductId = treats, Quantity = 5, UnitPrice = 1200 });
            await SalesFor(s).SaveAsync(req, Clerk, null);
        }
        return (cs, seed, treats);
    }

    [Fact]
    public async Task Forecast_reads_steady_demand_names_its_method_and_gives_a_run_out_date()
    {
        var (cs, seed, _) = await SeededHistory();
        await using var db = NewContext(cs);
        var rows = await new AnalyticsService(db, new SystemClock()).ForecastAsync(default);
        var food = rows.Single(r => r.ProductId == seed.ProductId);
        Assert.InRange(food.WeeklyDemand, 8, 12);
        Assert.NotEqual("Unknown", food.Confidence);
        Assert.False(string.IsNullOrEmpty(food.Method));
        Assert.NotNull(food.RunsOutOn);
        Assert.Equal(500 - 120, food.OnHand);
    }

    [Fact]
    public async Task Products_bought_together_show_up_as_a_rule_with_full_confidence()
    {
        var (cs, seed, treats) = await SeededHistory();
        await using var db = NewContext(cs);
        var rules = await new AnalyticsService(db, new SystemClock()).SoldTogetherAsync(default);
        Assert.Contains(rules, r => r.Consequent == "Dog Treats" && r.Antecedent.SequenceEqual(new[] { "Adult Dog Food 20kg" }) && r.Confidence == 1.0);
    }

    [Fact]
    public async Task Sale_advice_stays_quiet_for_a_normal_quantity_and_speaks_up_for_a_keying_slip()
    {
        var (cs, seed, _) = await SeededHistory();
        await using var db = NewContext(cs);
        var svc = new AnalyticsService(db, new SystemClock());

        var normal = await svc.SaleAdviceAsync(seed.CustomerId, [new SaleLineIn(seed.ProductId, 10)], default);
        Assert.DoesNotContain(normal, a => a.Kind == "quantity");

        var slip = await svc.SaleAdviceAsync(seed.CustomerId, [new SaleLineIn(seed.ProductId, 1000)], default);
        var line = Assert.Single(slip, a => a.Kind == "quantity");
        Assert.Contains("well outside what usually goes out", line.Text);
    }

    [Fact]
    public async Task Sale_advice_warns_when_a_sale_would_leave_less_than_a_delivery_takes_to_arrive()
    {
        var (cs, seed, _) = await SeededHistory();
        await using var db = NewContext(cs);
        // 380 on hand, about 10 a week: selling 370 leaves 10, roughly a week of cover against a 4-week delivery.
        var advice = await new AnalyticsService(db, new SystemClock()).SaleAdviceAsync(null, [new SaleLineIn(seed.ProductId, 370)], default);
        Assert.Contains(advice, a => a.Kind == "stockout" && a.Text.Contains("Worth reordering"));
    }

    [Fact]
    public async Task With_no_history_there_is_nothing_to_say()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using var db = NewContext(cs);
        var svc = new AnalyticsService(db, new SystemClock());
        Assert.Empty(await svc.ForecastAsync(default));
        Assert.Empty(await svc.SoldTogetherAsync(default));
        Assert.Empty(await svc.UnusualMovementsAsync(default));
        Assert.Empty(await svc.SaleAdviceAsync(seed.CustomerId, [new SaleLineIn(seed.ProductId, 5)], default));
    }

    [Fact]
    public async Task Suggested_order_only_lists_products_this_supplier_has_sent_before()
    {
        var (cs, seed, _) = await SeededHistory();
        int supplierId;
        await using (var db = NewContext(cs))
        {
            var sup = new Supplier { Name = "AgroFeed" }; db.Suppliers.Add(sup); await db.SaveChangesAsync(); supplierId = sup.Id;
            db.PurchaseOrders.Add(new PurchaseOrder { PoNumber = "PO-1", SupplierId = supplierId, OrderDate = DateOnly.FromDateTime(DateTime.UtcNow), TotalAmount = 85000,
                Items = [new PurchaseOrderItem { ProductId = seed.ProductId, Quantity = 10, UnitCost = 8500 }] });
            await db.SaveChangesAsync();
        }
        await using var q = NewContext(cs);
        var svc = new AnalyticsService(q, new SystemClock());
        var list = await svc.SuggestOrderAsync(supplierId, default);
        // 380 on hand against ~40 a fortnight-ish demand: covered, so nothing to suggest; and Treats is never listed (this supplier never sent it).
        Assert.DoesNotContain(list, l => l.Product == "Dog Treats");
    }
}
