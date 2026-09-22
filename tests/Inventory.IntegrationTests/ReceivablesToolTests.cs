using System.Text.Json;
using Inventory.Application.Ai;
using Inventory.Application.Dashboard;
using Inventory.Infrastructure.Services;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

[Collection("mysql")]
public class ReceivablesToolTests(MySqlFixture mysql)
{
    [Fact]
    public async Task The_receivables_tool_returns_every_open_invoice_with_totals_already_added_up()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 50);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // One overdue, one due next month (outside "this month"), one with no due date.
        foreach (var due in new DateOnly?[] { today.AddDays(-10), today.AddDays(40), null })
        {
            await using var s = NewContext(cs);
            var r = Sale(seed, 1, vat: 0); r.DueDate = due;
            await SalesFor(s).SaveAsync(r, Clerk, null);
        }

        await using var db = NewContext(cs);
        var clock = new SystemClock();
        var json = await new AiToolbox(db, clock, new OverviewQueries(db, clock)).ExecuteAsync("receivables", "{}", default);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(3 * 11500m, root.GetProperty("totalOwedByCustomers").GetDecimal());
        Assert.Equal(11500m, root.GetProperty("totalOverdue").GetDecimal());
        Assert.Equal(11500m, root.GetProperty("totalNotYetDue").GetDecimal());   // the invoice due next month is not lost
        var c = Assert.Single(root.GetProperty("customers").EnumerateArray());
        Assert.Equal(2, c.GetProperty("openInvoices").GetArrayLength());          // the third has no due date and shows only in "owes"
    }
}
