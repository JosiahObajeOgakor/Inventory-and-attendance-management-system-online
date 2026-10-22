using Inventory.Application.Abstractions;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>WhatsApp orders draw on every warehouse and branch: stock shown counts them all, and the sale is booked where the goods really are.</summary>
[Collection("mysql")]
public class OnlineOrderWarehouseTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();

    [Fact]
    public async Task Stock_in_another_branch_is_offered_and_the_order_is_booked_to_the_warehouse_that_can_fill_it()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 3);                               // the default warehouse holds only 3
        int second;
        await using (var db = NewContext(cs))
        {
            var w = new Warehouse { Name = "Lekki branch store" };
            db.Warehouses.Add(w); await db.SaveChangesAsync();
            second = w.Id;
            db.StockBatches.Add(new StockBatch { ProductId = seed.ProductId, WarehouseId = second, BatchNumber = "L1", QuantityOnHand = 20 });
            await db.SaveChangesAsync();
        }
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", DefaultWarehouseId: seed.WarehouseId));
        await using var read = NewContext(cs);

        var available = await new StockAvailabilityService(read, co, Clock, Options.Create(new SalesAssistantOptions())).AvailableAsync([seed.ProductId], null, default);
        Assert.Equal(23, available[seed.ProductId]);                             // 3 here + 20 at the other branch, not just the 3

        Assert.Equal(seed.WarehouseId, await WarehouseChooser.ForOrderAsync(read, co, Clock, [(seed.ProductId, 2)], default));    // the default can fill it
        Assert.Equal(second, await WarehouseChooser.ForOrderAsync(read, co, Clock, [(seed.ProductId, 10)], default));              // only the other branch can
        Assert.Equal(second, await WarehouseChooser.ForOrderAsync(read, co, Clock, [(seed.ProductId, 22)], default));              // spread over both: the one holding most
    }

    [Fact]
    public async Task A_business_with_no_default_warehouse_still_gets_a_warehouse_for_online_orders()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 5);
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", DefaultWarehouseId: 0));
        await using var read = NewContext(cs);
        Assert.Equal(seed.WarehouseId, await WarehouseChooser.ForOrderAsync(read, co, Clock, [(seed.ProductId, 1)], default));
    }
}
