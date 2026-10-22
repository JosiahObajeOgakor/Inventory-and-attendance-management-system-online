using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;

namespace Inventory.IntegrationTests;

[Collection("api")]
public class DebtorsAndShopTests(ApiFixture api)
{
    [Fact]
    public async Task The_debtors_list_shows_everyone_who_owes_biggest_first_with_totals_for_all_of_them()
    {
        var (warehouse, product, _) = await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        // two credit sales: one small, one large
        foreach (var (name, qty) in new[] { ("Debtor Small", 1), ("Debtor Large", 4) })
        {
            var c = await admin.PostAsJsonAsync("/api/customers", new { name, customerType = "Retailer", phone = "0803" + Random.Shared.Next(1000000, 9999999), rebatePerUnit = 0, creditLimit = 0 });
            c.EnsureSuccessStatusCode();
            var id = (await c.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            (await admin.PostAsJsonAsync("/api/sales", new { customerId = id, warehouseId = warehouse, priceTier = "Retailer", paymentMethod = "Credit", vatRate = 0, paidNow = 0,
                lines = new[] { new { productId = product, quantity = qty, unitPrice = 10000 } } })).EnsureSuccessStatusCode();
        }

        var page = await admin.GetFromJsonAsync<JsonElement>("/api/debtors?pageSize=200");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var owed = items.Select(i => i.GetProperty("owed").GetDecimal()).ToList();
        Assert.All(owed, o => Assert.True(o > 0));
        Assert.Equal(owed.OrderByDescending(o => o), owed);                                 // biggest first
        Assert.Equal(owed.Sum(), page.GetProperty("totalOwed").GetDecimal());
        var large = items.Single(i => i.GetProperty("name").GetString() == "Debtor Large");
        Assert.Equal(1, large.GetProperty("unpaidInvoices").GetInt32());
        Assert.Equal(0, large.GetProperty("daysOutstanding").GetInt32());

        var found = await admin.GetFromJsonAsync<JsonElement>("/api/debtors?search=debtor%20small");
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        var byName = await admin.GetFromJsonAsync<JsonElement>("/api/debtors?sort=name&pageSize=200");
        var names = byName.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.OrderBy(n => n).ToList(), names);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().GetAsync("/api/debtors")).StatusCode);
    }

    [Fact]
    public async Task The_website_shop_lists_in_stock_products_with_prices_and_no_stock_counts()
    {
        var r = await api.Client().GetAsync("/api/webchat/products");
        r.EnsureSuccessStatusCode();
        var list = (await r.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var dog = Assert.Single(list, p => p.GetProperty("name").GetString() == "Adult Dog Food");
        Assert.Equal(11500m, dog.GetProperty("price").GetDecimal());
        Assert.False(dog.TryGetProperty("available", out _));                                   // exact stock stays private
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/api/webchat/products/{dog.GetProperty("id").GetInt32()}/image")).StatusCode);
    }
}
