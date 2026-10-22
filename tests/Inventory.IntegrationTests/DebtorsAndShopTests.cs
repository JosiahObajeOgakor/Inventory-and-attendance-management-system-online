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

        // the sister store has its own shelf; a store that isn't listed is simply not there
        Assert.True((await api.Client().GetAsync("/api/webchat/products?store=candid")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync("/api/webchat/products?store=k9cravings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync("/api/webchat/products?store=inventory_identity")).StatusCode);
    }

    [Fact]
    public async Task A_review_appears_on_the_website_only_after_staff_approve_it_and_shows_only_the_first_name()
    {
        var anon = api.Client();
        var text = "Lovely kibble, my dog finished the bowl " + Guid.NewGuid().ToString("N")[..6];
        Assert.False((await anon.PostAsJsonAsync("/api/webchat/reviews", new { name = "Ada Obi", rating = 5, text, consentToPublish = false })).IsSuccessStatusCode);   // no consent, no review
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/webchat/reviews", new { name = "Ada Obi", place = "Ikeja", rating = 5, text, consentToPublish = true })).StatusCode);
        Assert.DoesNotContain(text, await anon.GetStringAsync("/api/webchat/reviews"));

        var admin = await api.LoginAsync("admin", "chewypets");
        var pending = (await admin.GetFromJsonAsync<JsonElement>("/api/website/reviews?status=Pending")).EnumerateArray().Single(r => r.GetProperty("text").GetString() == text);
        (await admin.PostAsync($"/api/website/reviews/{pending.GetProperty("id").GetInt32()}/approve", null)).EnsureSuccessStatusCode();

        var shown = (await anon.GetFromJsonAsync<JsonElement>("/api/webchat/reviews")).EnumerateArray().Single(r => r.GetProperty("text").GetString() == text);
        Assert.Equal("Ada", shown.GetProperty("name").GetString());                                  // first name only
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/website/reviews")).StatusCode);
    }

    [Fact]
    public async Task A_product_page_and_the_visit_counter_work_without_signing_in()
    {
        var anon = api.Client();
        var list = (await anon.GetFromJsonAsync<JsonElement>("/api/webchat/products")).EnumerateArray().ToList();
        var id = list.First().GetProperty("id").GetInt32();
        var page = await anon.GetFromJsonAsync<JsonElement>($"/api/webchat/products/{id}");
        Assert.True(page.GetProperty("inStock").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/webchat/products/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/webchat/track", new { @event = "site_view", store = "chewypets" })).StatusCode);
        // online payment needs a payment provider; without one the visitor is told plainly to use WhatsApp
        var pay = await anon.PostAsJsonAsync("/api/webchat/checkout", new { name = "Ada", phone = "08031234567", lines = new[] { new { productId = id, quantity = 1 } } });
        Assert.Contains("WhatsApp", await pay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_website_knows_both_stores_and_where_each_takes_orders()
    {
        var stores = (await api.Client().GetFromJsonAsync<JsonElement>("/api/webchat/stores")).EnumerateArray().ToList();
        Assert.Equal(new[] { "chewypets", "candid" }, stores.Select(s => s.GetProperty("key").GetString()).ToArray());
        Assert.All(stores, s => Assert.False(string.IsNullOrEmpty(s.GetProperty("name").GetString())));
    }
}
