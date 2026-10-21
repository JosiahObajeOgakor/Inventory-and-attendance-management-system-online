using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>
/// Every list screen sends ?page=&amp;pageSize=&amp;search=. These tests go through the real HTTP pipeline, because the bug they guard against lived there:
/// an action parameter called "page" made ASP.NET look for "page.Page", "page.Search"… and silently bind nothing, so Next always showed page 1 and search never filtered.
/// They use the Candid Purrfect schema so they don't disturb the other API tests' ChewyPets data.
/// </summary>
[Collection("api")]
public class ListBindingTests(ApiFixture api)
{
    [Fact]
    public void No_endpoint_names_a_query_object_after_one_of_its_own_fields()
    {
        var offenders = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetParameters().Select(p => (m, p)))
            .Where(x => x.p.GetCustomAttribute<FromQueryAttribute>() is not null && x.p.ParameterType.IsClass && x.p.ParameterType != typeof(string))
            .Where(x => x.p.ParameterType.GetProperties().Any(prop => string.Equals(prop.Name, x.p.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.m.DeclaringType!.Name}.{x.m.Name}({x.p.Name})").ToList();
        Assert.Empty(offenders);
    }

    private async Task<(HttpClient Admin, int Warehouse, int[] Products, int[] Customers)> CandidAsync()
    {
        using (var scope = api.Factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
            await using var db = registry.CreateFor(registry.Find("candid")!);
            if (!db.Products.Any())
            {
                var cat = new Domain.Entities.Category { Name = "Cat Food" };
                var wh = new Domain.Entities.Warehouse { Name = "Candid Store" };
                db.AddRange(cat, wh); await db.SaveChangesAsync();
                var names = new[] { "Kitten Formula 2kg", "Adult Cat Chow 10kg", "Senior Cat Mix 5kg" };
                var products = names.Select((n, i) => new Domain.Entities.Product { Sku = $"C-{i}", Name = n, CategoryId = cat.Id, PriceRetail = 5000 + i * 1000, SellingPrice = 5000 + i * 1000, ReorderLevel = 2 }).ToList();
                db.AddRange(products);
                db.AddRange(new Domain.Entities.Customer { Name = "Ngozi Eze", CustomerType = "Retailer" }, new Domain.Entities.Customer { Name = "Musa Bello", CustomerType = "Retailer" });
                await db.SaveChangesAsync();
                foreach (var p in products) db.StockBatches.Add(new Domain.Entities.StockBatch { ProductId = p.Id, WarehouseId = wh.Id, BatchNumber = "MAIN", QuantityOnHand = 100 });
                await db.SaveChangesAsync();
            }
        }
        var admin = await api.LoginAsync("admin", "candid");
        var warehouse = JsonDocument.Parse(await admin.GetStringAsync("/api/warehouses")).RootElement[0].GetProperty("id").GetInt32();
        var productIds = JsonDocument.Parse(await admin.GetStringAsync("/api/products?pageSize=50")).RootElement.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray();
        // Ngozi first, then Musa — picked by name, not by the lookup's (alphabetical) order.
        var lookup = JsonDocument.Parse(await admin.GetStringAsync("/api/customers/lookup")).RootElement.EnumerateArray().ToList();
        int IdOf(string name) => lookup.First(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetInt32();
        var customerIds = new[] { IdOf("Ngozi Eze"), IdOf("Musa Bello") };
        return (admin, warehouse, productIds, customerIds);
    }

    [Fact]
    public async Task Page_page_size_and_search_reach_the_server()
    {
        var (admin, _, _, _) = await CandidAsync();
        var p1 = JsonDocument.Parse(await admin.GetStringAsync("/api/products?page=1&pageSize=1")).RootElement;
        var p2 = JsonDocument.Parse(await admin.GetStringAsync("/api/products?page=2&pageSize=1")).RootElement;
        Assert.Equal(1, p1.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, p2.GetProperty("page").GetInt32());                              // "Next" really asks for page 2…
        Assert.Single(p2.GetProperty("items").EnumerateArray());
        Assert.NotEqual(p1.GetProperty("items")[0].GetProperty("id").GetInt32(), p2.GetProperty("items")[0].GetProperty("id").GetInt32());   // …and gets different rows

        var kitten = JsonDocument.Parse(await admin.GetStringAsync("/api/products?search=kitten")).RootElement;
        Assert.Equal(1, kitten.GetProperty("total").GetInt32());                          // search filters
        Assert.Equal("Kitten Formula 2kg", kitten.GetProperty("items")[0].GetProperty("name").GetString());
        var stock = JsonDocument.Parse(await admin.GetStringAsync("/api/inventory/batches?page=2&pageSize=2")).RootElement;
        Assert.Equal((2, 2), (stock.GetProperty("page").GetInt32(), stock.GetProperty("pageSize").GetInt32()));   // the Stock screen's own list
    }

    [Fact]
    public async Task Sales_can_be_searched_by_customer_and_the_newest_sale_comes_first()
    {
        var (admin, warehouse, products, customers) = await CandidAsync();
        async Task<string> Sell(int customer, string date)
        {
            var r = await admin.PostAsJsonAsync("/api/sales", new { customerId = customer, warehouseId = warehouse, saleDate = date, priceTier = "Retailer", paymentMethod = "Cash", vatRate = 0, paidNow = 5000,
                lines = new[] { new { productId = products[0], quantity = 1, unitPrice = 5000 } } });
            r.EnsureSuccessStatusCode();
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoiceNumber").GetString()!;
        }
        var july = await Sell(customers[0], "2026-07-10");
        var october = await Sell(customers[1], "2026-10-01");
        var march = await Sell(customers[0], "2026-03-05");                               // recorded last, but dated earliest

        var all = JsonDocument.Parse(await admin.GetStringAsync("/api/sales?pageSize=50")).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("invoiceNumber").GetString()).Where(n => n == july || n == october || n == march).ToList();
        Assert.Equal([october, july, march], all);                                        // by date, newest first — not alphabetical, not entry order

        var ngozi = JsonDocument.Parse(await admin.GetStringAsync("/api/sales?search=ngozi&pageSize=50")).RootElement;
        var names = ngozi.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("customer").GetString()).Distinct().ToList();
        Assert.Equal(["Ngozi Eze"], names);                                               // searching a customer's name finds only their sales
        Assert.Contains(ngozi.GetProperty("items").EnumerateArray(), i => i.GetProperty("invoiceNumber").GetString() == march);
    }
}
