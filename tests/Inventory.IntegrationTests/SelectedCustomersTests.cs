using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>A broadcast (a survey link, an offer) can go to customers picked one by one — still only those who agreed to messages.</summary>
[Collection("api")]
public class SelectedCustomersTests(ApiFixture api)
{
    [Fact]
    public async Task A_broadcast_to_selected_customers_reaches_only_the_ticked_ones_who_opted_in()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        int yes, noEmail, notAsked, other;
        using (var scope = api.Factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
            await using var db = registry.CreateFor(registry.Find(K9Seed.Company)!);
            var a = new Customer { Name = "Survey Yes", Phone = "08030000001", Email = "yes@survey.example", MarketingStatus = MarketingStatuses.OptedIn, CustomerType = "Retailer" };
            var b = new Customer { Name = "Survey NoEmail", Phone = "08030000002", MarketingStatus = MarketingStatuses.OptedIn, CustomerType = "Retailer" };
            var c = new Customer { Name = "Survey NotAsked", Phone = "08030000003", Email = "na@survey.example", CustomerType = "Retailer" };
            var d = new Customer { Name = "Survey Other", Phone = "08030000004", Email = "other@survey.example", MarketingStatus = MarketingStatuses.OptedIn, CustomerType = "Retailer" };
            db.AddRange(a, b, c, d); await db.SaveChangesAsync();
            (yes, noEmail, notAsked, other) = (a.Id, b.Id, c.Id, d.Id);
        }
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);

        var created = await admin.PostAsJsonAsync("/api/apps/email-marketing/campaign", new
        {
            parentId = (int?)null,
            fields = new { title = "October survey", subject = "Tell us how we did", audience = "Selected customers", customers = new[] { yes, noEmail, notAsked }, message = "Hello {name}, 1 minute: https://example.test/survey" },
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var count = await admin.PostAsJsonAsync($"/api/apps/email-marketing/records/{id}/actions/count", new { text = (string?)null });
        count.EnsureSuccessStatusCode();
        var msg = (await count.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.StartsWith("1 of 2 opted-in", msg);                       // "yes" has an email; "noEmail" doesn't; "other" was never ticked
        Assert.Contains("1 of the 3 you ticked haven't agreed", msg);

        // nothing ticked: a plain refusal, not a send to everybody
        var empty = await admin.PostAsJsonAsync("/api/apps/email-marketing/campaign", new
        {
            parentId = (int?)null,
            fields = new { title = "Nobody", subject = "x", audience = "Selected customers", message = "Hi {name}" },
        });
        var emptyId = (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var refused = await admin.PostAsJsonAsync($"/api/apps/email-marketing/records/{emptyId}/actions/count", new { text = (string?)null });
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("Tick the customers", await refused.Content.ReadAsStringAsync());
        _ = other;
    }
}
