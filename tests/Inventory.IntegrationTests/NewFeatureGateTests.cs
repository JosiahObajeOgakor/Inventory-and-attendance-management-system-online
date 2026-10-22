using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;

namespace Inventory.IntegrationTests;

/// <summary>Every business now has all the newer features (Campaigns, Marketing, Accounting, Branches, Time Off and the other apps); only K9 Cravings gets demo records.</summary>
[Collection("api")]
public class NewFeatureGateTests(ApiFixture api)
{
    [Fact]
    public async Task Every_business_has_every_new_feature()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        var clients = new List<HttpClient> { await api.LoginAsync("k9admin", K9Seed.Company), await api.LoginAsync("admin", "chewypets"), await api.LoginAsync("admin", "candid") };
        foreach (var c in clients)
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/campaigns")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/campaigns/insights")).StatusCode);
            var apps = (await c.GetFromJsonAsync<JsonElement>("/api/apps/enabled")).EnumerateArray().Select(a => a.GetString()).ToList();
            foreach (var app in new[] { "email-marketing", "surveys", "blog", "sign", "time-off", "website" }) Assert.Contains(app, apps);
            foreach (var path in new[] { "/api/branches", "/api/gl/accounts", "/api/tax/vat/2026/1", "/api/banking/accounts", "/api/budgets", "/api/assets", "/api/currencies", "/api/approvals/mine", "/api/time-off", "/api/sales" })
                Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(path)).StatusCode);
        }
    }
}