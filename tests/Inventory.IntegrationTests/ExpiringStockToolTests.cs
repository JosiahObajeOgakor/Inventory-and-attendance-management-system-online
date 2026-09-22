using System.Text.Json;
using Inventory.Application.Ai;
using Inventory.Application.Dashboard;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

[Collection("mysql")]
public class ExpiringStockToolTests(MySqlFixture mysql)
{
    [Fact]
    public async Task The_tool_names_expired_and_soon_to_expire_batches_with_product_warehouse_and_quantity()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var db = NewContext(cs))
        {
            db.StockBatches.AddRange(
                new StockBatch { ProductId = seed.ProductId, WarehouseId = seed.WarehouseId, BatchNumber = "OLD-1", QuantityOnHand = 4, ExpiryDate = today.AddDays(-20) },
                new StockBatch { ProductId = seed.ProductId, WarehouseId = seed.WarehouseId, BatchNumber = "SOON-1", QuantityOnHand = 6, ExpiryDate = today.AddDays(12) },
                new StockBatch { ProductId = seed.ProductId, WarehouseId = seed.WarehouseId, BatchNumber = "LATER", QuantityOnHand = 9, ExpiryDate = today.AddDays(200) });
            await db.SaveChangesAsync();
        }

        await using var q = NewContext(cs);
        var clock = new SystemClock();
        var json = await new AiToolbox(q, clock, new OverviewQueries(q, clock)).ExecuteAsync("expiring_stock", "{}", default);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal(4, root.GetProperty("expiredUnits").GetInt32());
        Assert.Equal(4 * 8500m, root.GetProperty("expiredCostValue").GetDecimal());
        Assert.Equal(6, root.GetProperty("expiringSoonUnits").GetInt32());
        var batches = root.GetProperty("batches").EnumerateArray().ToList();
        Assert.Equal(2, batches.Count);                                  // the batch expiring in 200 days is not listed
        Assert.Equal("OLD-1", batches[0].GetProperty("batch").GetString());
        Assert.Equal("Adult Dog Food 20kg", batches[0].GetProperty("product").GetString());
        Assert.Equal("Main", batches[0].GetProperty("warehouse").GetString());
        Assert.Equal("expired", batches[0].GetProperty("status").GetString());
        Assert.Contains(AiToolbox.Specs, s => s.Name == "expiring_stock");
    }
}
