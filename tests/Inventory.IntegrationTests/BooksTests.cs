using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;

namespace Inventory.IntegrationTests;

/// <summary>The books over real HTTP: documents already recorded become balanced journals, once, and the statements read from them.</summary>
[Collection("api")]
public class BooksTests(ApiFixture api)
{
    [Fact]
    public async Task Recorded_sales_expenses_and_voids_reach_the_books_once_and_they_balance()
    {
        var (warehouseId, productId, customerId) = await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(1)).ToString("yyyy-MM-dd");

        var before = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/profit-and-loss?from={today}&to={today}");
        var incomeBefore = before.GetProperty("totalIncome").GetDecimal();

        var sale = await admin.PostAsJsonAsync("/api/sales", new { customerId, warehouseId, priceTier = "Retailer", paymentMethod = "Cash", vatRate = 7.5m, paidNow = 1000, lines = new[] { new { productId, quantity = 1, unitPrice = 10000 } } });
        sale.EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/expenses", new { category = "Rent", expenseDate = today, amount = 2500, note = "Shop" })).EnsureSuccessStatusCode();

        var pl = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/profit-and-loss?from={today}&to={today}");
        Assert.True(pl.GetProperty("totalIncome").GetDecimal() >= incomeBefore + 10000 - 1);   // the sale's revenue (net of VAT) is in
        var again = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/profit-and-loss?from={today}&to={today}");
        Assert.Equal(pl.GetProperty("totalIncome").GetDecimal(), again.GetProperty("totalIncome").GetDecimal());   // reading again posts nothing twice

        var tb = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/trial-balance?asOf={today}");
        Assert.True(tb.GetProperty("balanced").GetBoolean());
        Assert.Equal(tb.GetProperty("totalDebit").GetDecimal(), tb.GetProperty("totalCredit").GetDecimal());

        var vat = await admin.GetFromJsonAsync<JsonElement>($"/api/tax/vat/{DateTime.UtcNow.Year}/{DateTime.UtcNow.Month}");
        Assert.True(vat.GetProperty("outputVat").GetDecimal() > 0);

        Assert.Equal(System.Net.HttpStatusCode.OK, (await admin.GetAsync("/api/branches/performance")).StatusCode);
    }

    [Fact]
    public async Task A_sale_edited_below_what_was_paid_follows_into_the_books_and_every_screen_and_export_still_opens()
    {
        var (warehouseId, productId, _) = await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(1)).ToString("yyyy-MM-dd");
        var c = await admin.PostAsJsonAsync("/api/customers", new { name = "Books Credit " + Guid.NewGuid().ToString("N")[..6], customerType = "Retailer", phone = "0805" + Random.Shared.Next(1000000, 9999999), rebatePerUnit = 0, creditLimit = 0 });
        c.EnsureSuccessStatusCode();
        var customerId = (await c.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // paid in full for 3, then edited down to 1: the extra ₦20,000 leaves the sale as a NEGATIVE "Account credit" payment (what broke Candid's books)
        var sale = await admin.PostAsJsonAsync("/api/sales", new { customerId, warehouseId, priceTier = "Retailer", paymentMethod = "Cash", vatRate = 0, paidNow = 30000, lines = new[] { new { productId, quantity = 3, unitPrice = 10000 } } });
        sale.EnsureSuccessStatusCode();
        var invoiceId = (await sale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoiceId").GetInt32();
        var pl1 = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/profit-and-loss?from={today}&to={today}");
        (await admin.PutAsJsonAsync($"/api/sales/{invoiceId}", new { discountPct = 0, vatRate = 0, deliveryFee = 0, lines = new[] { new { productId, quantity = 1, unitPrice = 10000 } } })).EnsureSuccessStatusCode();

        var pl2 = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/profit-and-loss?from={today}&to={today}");
        Assert.Equal(pl1.GetProperty("totalIncome").GetDecimal() - 20000, pl2.GetProperty("totalIncome").GetDecimal());   // the edit reached the books
        var tb = await admin.GetFromJsonAsync<JsonElement>($"/api/gl/trial-balance?asOf={today}");
        Assert.True(tb.GetProperty("balanced").GetBoolean());
        var credits = tb.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "2200");
        Assert.True(credits.GetProperty("balance").GetDecimal() >= 20000);                          // the ₦20,000 waits as the customer's credit, not as cash

        foreach (var url in new[] { "/api/gl/balance-sheet", "/api/gl/journals", "/api/banking/accounts", $"/api/tax/vat/{DateTime.UtcNow.Year}/{DateTime.UtcNow.Month}" })
            Assert.Equal(System.Net.HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
        var pdf = await admin.GetAsync($"/api/gl/statements?which=all&format=pdf&from={today}&to={today}");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        var xlsx = await admin.GetAsync($"/api/gl/statements?which=all&format=xlsx&from={today}&to={today}");
        xlsx.EnsureSuccessStatusCode();
        Assert.Equal((byte)'P', (await xlsx.Content.ReadAsByteArrayAsync())[0]);                    // a zip: a real .xlsx
    }
}
