using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>A small business of its own (K9 Cravings) for tests that make sales, so the other tests' ChewyPets and Candid figures are left alone.</summary>
public static class K9Seed
{
    public const string Company = "k9cravings";

    public static async Task<(int Main, int Product, int Customer)> EnsureAsync(ApiFixture api, params (string User, string Role)[] users)
    {
        using var scope = api.Factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var (name, role) in users)
        {
            if (await mgr.FindByNameAsync(name) is not null) continue;
            var u = new AppUser { UserName = name, FullName = name.ToUpperInvariant(), PasswordHash = LegacyAwarePasswordHasher.MakeLegacyHash(ApiFixture.Password), IsActive = true, CompanyAccess = Company };
            Assert.True((await mgr.CreateAsync(u)).Succeeded);
            await mgr.AddToRoleAsync(u, role);
        }
        var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
        await using var db = registry.CreateFor(registry.Find(Company)!);
        if (!db.Products.Any())
        {
            var cat = new Domain.Entities.Category { Name = "Treats" };
            var wh = new Domain.Entities.Warehouse { Name = "K9 Main" };
            db.AddRange(cat, wh); await db.SaveChangesAsync();
            var p = new Domain.Entities.Product { Sku = "K9-1", Name = "Jerky", CategoryId = cat.Id, CostPrice = 4000, PriceRetail = 10000, SellingPrice = 10000, ReorderLevel = 2 };
            db.AddRange(p, new Domain.Entities.Customer { Name = "K9 Buyer", CustomerType = "Retailer" });
            await db.SaveChangesAsync();
            db.StockBatches.Add(new Domain.Entities.StockBatch { ProductId = p.Id, WarehouseId = wh.Id, BatchNumber = "K1", QuantityOnHand = 500 });
            await db.SaveChangesAsync();
        }
        return (db.Warehouses.First().Id, db.Products.First().Id, db.Customers.First().Id);
    }
}
