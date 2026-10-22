using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;

namespace Inventory.IntegrationTests;

/// <summary>Campaigns and the Marketing apps belong to the businesses that have Marketing switched on (K9 Cravings); the others get a plain 403 and no menu entries.</summary>
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
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/campaigns")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/apps/email-marketing")).StatusCode);
            var apps = (await other.GetFromJsonAsync<JsonElement>("/api/apps/enabled")).EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.DoesNotContain("email-marketing", apps);
            Assert.DoesNotContain("surveys", apps);
            Assert.Empty(apps);
            foreach (var path in new[] { "/api/branches", "/api/gl/accounts", "/api/tax/vat/2026/1", "/api/banking/accounts", "/api/budgets", "/api/assets", "/api/currencies", "/api/approvals/mine", "/api/time-off" })
                Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/sales")).StatusCode);   // the everyday system is untouched
        }
    }
}
