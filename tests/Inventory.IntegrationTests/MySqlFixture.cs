using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Sales;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace Inventory.IntegrationTests;

/// <summary>A real MySQL 8 in Docker; each test class gets its own schema so tests never see each other's rows.</summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4").WithUsername("root").WithPassword("test-pw-only").Build();
    private int _schemaCounter;

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty company schema (case-insensitive, accent-sensitive collation) and returns its connection string.</summary>
    public async Task<string> NewSchemaAsync()
    {
        var name = $"co_{Interlocked.Increment(ref _schemaCounter)}_{Guid.NewGuid():N}"[..24];
        await using var conn = new MySqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_ci", conn);
        await cmd.ExecuteNonQueryAsync();
        var csb = new MySqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = true, MaximumPoolSize = 50 };
        await using (var ctx = NewContext(csb.ConnectionString)) await ctx.Database.EnsureCreatedAsync();
        return csb.ConnectionString;
    }

    public static BusinessDbContext NewContext(string cs) =>
        new(new DbContextOptionsBuilder<BusinessDbContext>().UseMySQL(cs).Options);

    public static SalesService SalesFor(BusinessDbContext db)
    {
        var clock = new SystemClock();
        var runner = new TransactionRunner(db, new MySqlErrorClassifier());
        return new SalesService(db, runner, new StockService(db, clock), clock,
            new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x")), new SaleRequestValidator());
    }

    public sealed record Seed(int ProductId, int WarehouseId, int CustomerId);

    public static async Task<Seed> SeedAsync(string cs, int stock, decimal balance = 0, string customerType = "Retailer")
    {
        await using var db = NewContext(cs);
        var cat = new Category { Name = "Dog Food" };
        var wh = new Warehouse { Name = "Main" };
        db.AddRange(cat, wh);
        await db.SaveChangesAsync();
        var product = new Product { Sku = "SKU-1", Name = "Adult Dog Food 20kg", CategoryId = cat.Id, CostPrice = 8500, PriceRetail = 11500, PriceWholesaler = 11000, PriceDistributor = 10500, SellingPrice = 11500, ReorderLevel = 5 };
        var customer = new Customer { Name = "PetMart", CustomerType = customerType, Balance = balance, RebateRatePct = 1m };
        db.AddRange(product, customer);
        await db.SaveChangesAsync();
        db.StockBatches.Add(new StockBatch { ProductId = product.Id, WarehouseId = wh.Id, BatchNumber = "B1", QuantityOnHand = stock });
        await db.SaveChangesAsync();
        return new Seed(product.Id, wh.Id, customer.Id);
    }

    public static SaleRequest Sale(Seed s, int qty, decimal price = 11500, decimal paid = 0, decimal vat = 7.5m) => new()
    {
        CustomerId = s.CustomerId, WarehouseId = s.WarehouseId, PriceTier = "Retailer", VatRate = vat, PaidNow = paid,
        Lines = [new SaleLineDto { ProductId = s.ProductId, Quantity = qty, UnitPrice = price }],
    };

    public static readonly CurrentUser Clerk = new(1, "David Okon", "CLERK");
}

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;
