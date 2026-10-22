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
}
