using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>Every search box, through the real HTTP pipeline and real MySQL: each way of looking something up finds it, several words narrow, and nonsense finds nothing.</summary>
[Collection("api")]
public class SearchTests(ApiFixture api)
{
    private static async Task<int> Count(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.IsSuccessStatusCode, $"{url} -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        if (j.ValueKind == JsonValueKind.Array) return j.GetArrayLength();
        if (j.TryGetProperty("page", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) return wrapped.GetProperty("total").GetInt32();   // expenses wrap the page
        return j.GetProperty("total").GetInt32();
    }

    private async Task Finds(HttpClient c, string path, params string[] searches)
    {
        foreach (var s in searches) Assert.True(await Count(c, $"{path}{(path.Contains('?') ? '&' : '?')}search={Uri.EscapeDataString(s)}") >= 1, $"{path} search '{s}' found nothing");
        Assert.Equal(0, await Count(c, $"{path}{(path.Contains('?') ? '&' : '?')}search=zzqxnothing"));
    }

    [Fact]
    public async Task Every_search_box_finds_things_by_every_obvious_means()
    {
        var (warehouse, product, _) = await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        int customerId, supplierId;
        using (var scope = api.Factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
            await using var db = registry.CreateFor(registry.Find(K9Seed.Company)!);
            var c = new Domain.Entities.Customer { Name = "Searchable Pets Ltd", ContactName = "Adaora Nwankwo", Phone = "+234 803 222 3344", Email = "adaora@searchable.example", Location = "Enugu", Address = "12 Zik Avenue", TaxId = "TIN-778899", CustomerType = "Wholesaler" };
            var s = new Domain.Entities.Supplier { Name = "Findable Feeds", ContactName = "Bola Ige", Phone = "0805 777 1212", Email = "bola@findable.example", Category = "Grain", Address = "Kano Road", TaxId = "TIN-112233" };
            db.AddRange(c, s); await db.SaveChangesAsync();
            (customerId, supplierId) = (c.Id, s.Id);
        }
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(1));

        var sale = await admin.PostAsJsonAsync("/api/sales", new { customerId, warehouseId = warehouse, priceTier = "Retailer", paymentMethod = "Bank Transfer", vatRate = 0, paidNow = 0,
            lines = new[] { new { productId = product, quantity = 3, unitPrice = 12345 } } });
        sale.EnsureSuccessStatusCode();
        var invoiceNumber = (await sale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoiceNumber").GetString()!;
        (await admin.PostAsJsonAsync("/api/purchases", new { supplierId, receiveNow = true, warehouseId = warehouse, paidNow = 0, paymentMethod = "Cash", vatRate = 0,
            lines = new[] { new { productId = product, quantity = 10, unitCost = 3333 } } })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/expenses", new { category = "Rent", expenseDate = today, amount = 98765, note = "Searchable shop rent" })).EnsureSuccessStatusCode();

        // products and stock
        await Finds(admin, "/api/products", "jerky", "k9-1", "treats", "jerky treats", "bag", "JERKY");
        await Finds(admin, "/api/inventory/batches", "jerky", "k9-1", "treats", "k9 main", "k1");
        await Finds(admin, "/api/inventory/movements", "jerky", "k9-1", "invoice", "k9 main", today.ToString("yyyy-MM"));

        // customers: name, contact person, phone however typed, email, place, address, tax id, type
        await Finds(admin, "/api/customers", "searchable", "adaora", "nwankwo", "adaora nwankwo", "08032223344", "+2348032223344", "2348032223344", "803 222", "adaora@searchable", "enugu", "zik avenue", "778899", "wholesaler", "pets enugu");
        await Finds(admin, "/api/customers/lookup", "searchable", "08032223344", "enugu", "adaora");
        // suppliers
        await Finds(admin, "/api/suppliers", "findable", "bola", "08057771212", "805 777", "bola@findable", "grain", "kano", "112233", "findable grain");

        // sales: number, customer, phone, product, amount, date, method, status
        await Finds(admin, "/api/sales", invoiceNumber, "searchable", "08032223344", "jerky", "k9-1", "37035", today.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM"), $"{today:dd/MM/yyyy}", "bank transfer", "unpaid", "searchable jerky");
        // purchases
        await Finds(admin, "/api/purchases", "findable", "08057771212", "jerky", "33330", today.ToString("yyyy-MM"), "received", "findable jerky");
        // expenses and the old ledger
        await Finds(admin, $"/api/expenses?from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}", "searchable", "rent", "98765", "shop rent");
        await Finds(admin, "/api/finance/ledger", "searchable", "findable");

        // the lists that have nothing in them yet still have to run their search without error
        foreach (var url in new[] { "/api/quotations?search=abc def", "/api/waybills?search=abc def", "/api/waybills/invoices?search=abc", "/api/price-book?search=jerky", "/api/price-book/history?search=jerky",
                                    "/api/rebates?search=abc", $"/api/attendance/events?from={today.AddDays(-5):yyyy-MM-dd}&to={today:yyyy-MM-dd}&search=abc", "/api/audit?search=sale", "/api/audit/logins?search=admin" })
            Assert.True((await admin.GetAsync(url)).IsSuccessStatusCode, url);
        Assert.True(await Count(admin, "/api/price-book?search=jerky") >= 1);
        Assert.True(await Count(admin, "/api/audit?search=SALE") >= 1);
    }
}
