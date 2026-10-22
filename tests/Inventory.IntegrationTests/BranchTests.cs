using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>Branches over real HTTP: the head office appears, warehouses move between branches, and a user tied to a branch can only sell from its warehouses.</summary>
[Collection("api")]
public class BranchTests(ApiFixture api)
{
    [Fact]
    public async Task A_user_tied_to_a_branch_sells_only_from_its_warehouses()
    {
        var (mainId, productId, customerId) = await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin), ("zaria.clerk", RoleNames.Clerk));
        int zariaId;
        using (var scope = api.Factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
            await using var db = registry.CreateFor(registry.Find(K9Seed.Company)!);
            var zaria = db.Warehouses.FirstOrDefault(w => w.Name == "Zaria store");
            if (zaria is null)
            {
                zaria = new Domain.Entities.Warehouse { Name = "Zaria store" };
                db.Warehouses.Add(zaria); await db.SaveChangesAsync();
                db.StockBatches.Add(new Domain.Entities.StockBatch { ProductId = productId, WarehouseId = zaria.Id, BatchNumber = "Z1", QuantityOnHand = 5 });
                await db.SaveChangesAsync();
            }
            zariaId = zaria.Id;
        }

        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/branches");
        var head = branches.EnumerateArray().Single(b => b.GetProperty("isHeadOffice").GetBoolean());
        Assert.Contains(head.GetProperty("warehouses").EnumerateArray(), w => w.GetProperty("id").GetInt32() == mainId);   // existing warehouses start at the head office

        var created = await admin.PostAsJsonAsync("/api/branches", new { code = "zar", name = "Zaria", active = true });
        created.EnsureSuccessStatusCode();
        var branchId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/branches", new { code = "ZAR", name = "Again", active = true })).StatusCode);   // codes are unique
        (await admin.PutAsJsonAsync($"/api/branches/warehouses/{zariaId}", new { branchId })).EnsureSuccessStatusCode();

        var staff = await admin.GetFromJsonAsync<JsonElement>("/api/branches/users");
        var clerkId = staff.EnumerateArray().Single(s => s.GetProperty("username").GetString() == "zaria.clerk").GetProperty("userId").GetInt32();
        (await admin.PutAsJsonAsync($"/api/branches/users/{clerkId}", new { branchId })).EnsureSuccessStatusCode();

        var clerk = await api.LoginAsync("zaria.clerk", K9Seed.Company);   // the branch is read at sign-in
        object Sale(int warehouse) => new { customerId, warehouseId = warehouse, priceTier = "Retailer", paymentMethod = "Cash", vatRate = 0, paidNow = 0, lines = new[] { new { productId, quantity = 1, unitPrice = 100 } } };
        var refused = await clerk.PostAsJsonAsync("/api/sales", Sale(mainId));                                         // another branch's warehouse is refused
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("own branch", await refused.Content.ReadAsStringAsync());
        (await clerk.PostAsJsonAsync("/api/sales", Sale(zariaId))).EnsureSuccessStatusCode();                         // their own is fine

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/branches")).StatusCode);                   // branch set-up is for admins
    }
}
