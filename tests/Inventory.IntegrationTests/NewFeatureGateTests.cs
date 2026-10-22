using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;

namespace Inventory.IntegrationTests;

/// <summary>K9 Cravings (the demo) has every new feature; ChewyPets and Candid Purrfect have only Marketing (Campaigns and the Marketing apps) and get a plain 403 for the rest.</summary>
[Collection("api")]
public class NewFeatureGateTests(ApiFixture api)
{
    [Fact]
    public async Task The_new_features_work_only_in_the_demo_business_for_now()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        var k9 = await api.LoginAsync("k9admin", K9Seed.Company);
        Assert.Equal(HttpStatusCode.OK, (await k9.GetAsync("/api/campaigns")).StatusCode);
        var k9Apps = (await k9.GetFromJsonAsync<JsonElement>("/api/apps/enabled")).EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("email-marketing", k9Apps);
        foreach (var path in new[] { "/api/branches", "/api/gl/accounts", "/api/budgets" }) Assert.Equal(HttpStatusCode.OK, (await k9.GetAsync(path)).StatusCode);

        foreach (var company in new[] { "chewypets", "candid" })
        {
            var other = await api.LoginAsync("admin", company);
            // Marketing is switched on for the live businesses: Campaigns and the Marketing apps, nothing else.
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/campaigns")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/campaigns/insights")).StatusCode);
            var apps = (await other.GetFromJsonAsync<JsonElement>("/api/apps/enabled")).EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("email-marketing", apps);
            Assert.Contains("surveys", apps);
            Assert.DoesNotContain("sign", apps);
            Assert.DoesNotContain("time-off", apps);
            Assert.DoesNotContain("website", apps);
            foreach (var path in new[] { "/api/branches", "/api/gl/accounts", "/api/tax/vat/2026/1", "/api/banking/accounts", "/api/budgets", "/api/assets", "/api/currencies", "/api/approvals/mine", "/api/time-off" })
                Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/sales")).StatusCode);   // the everyday system is untouched
        }
    }
}
