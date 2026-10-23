using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests;

/// <summary>A digital marketer: marketing screens only, and nothing goes in front of customers without an admin's approval.</summary>
[Collection("api")]
public class MarketerTests(ApiFixture api)
{
    [Fact]
    public async Task A_marketer_reaches_marketing_only()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin), ("k9marketer", RoleNames.Marketer));
        var m = await api.LoginAsync("k9marketer", K9Seed.Company);

        Assert.Equal(HttpStatusCode.OK, (await m.GetAsync("/api/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await m.GetAsync("/api/apps/whatsapp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await m.GetAsync("/api/website/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await m.GetAsync("/api/apps/options/customer")).StatusCode);
        foreach (var closed in new[] { "/api/sales", "/api/customers", "/api/products", "/api/finance/summary", "/api/users", "/api/trade-partners", "/api/apps/fleet", "/api/apps/approvals", "/api/debtors" })
            Assert.Equal(HttpStatusCode.Forbidden, (await m.GetAsync(closed)).StatusCode);

        var enabled = await (await m.GetAsync("/api/apps/enabled")).Content.ReadFromJsonAsync<string[]>();
        Assert.DoesNotContain("fleet", enabled!);
        Assert.Contains("whatsapp", enabled!);
        // running a built-in campaign for real is the admin's
        var list = await (await m.GetAsync("/api/campaigns")).Content.ReadFromJsonAsync<JsonElement>();
        var first = list[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await m.PostAsJsonAsync($"/api/campaigns/{first}/run", new { })).StatusCode);
    }

    [Fact]
    public async Task What_a_marketer_puts_live_waits_for_an_admin_who_approves_or_rejects_it()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin), ("k9marketer", RoleNames.Marketer));
        int customer;
        using (var scope = api.Factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
            await using var db = registry.CreateFor(registry.Find(K9Seed.Company)!);
            var c = new Customer { Name = "Marketer Target", Phone = "08035550001", MarketingStatus = MarketingStatuses.OptedIn, CustomerType = "Retailer" };
            db.Add(c); await db.SaveChangesAsync(); customer = c.Id;
        }
        var m = await api.LoginAsync("k9marketer", K9Seed.Company);
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);

        // a WhatsApp broadcast: "send" becomes a request, nothing is queued
        var created = await m.PostAsJsonAsync("/api/apps/whatsapp/campaign", new { parentId = (int?)null,
            fields = new { title = "Weekend offer", audience = "Selected customers", customers = new[] { customer }, message = "Hello {name}, fresh stock is in." } });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var send = await m.PostAsJsonAsync($"/api/apps/whatsapp/records/{id}/actions/send", new { text = (string?)null });
        send.EnsureSuccessStatusCode();
        Assert.Contains("approval", (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        var rec = await (await m.GetAsync($"/api/apps/whatsapp/records/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Awaiting approval", rec.GetProperty("status").GetString());
        Assert.False((await m.PutAsJsonAsync($"/api/apps/whatsapp/campaign/{id}", new { parentId = (int?)null, fields = new { title = "Changed", message = "x" } })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await m.PostAsJsonAsync($"/api/apps/whatsapp/records/{id}/approve", new { })).StatusCode);

        var waiting = await (await admin.GetAsync("/api/apps/approvals")).Content.ReadFromJsonAsync<JsonElement>();
        var item = waiting.EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == id);
        Assert.Equal("send it to customers", item.GetProperty("what").GetString());
        Assert.Contains(item.GetProperty("content").EnumerateArray(), f => f.GetProperty("value").GetString()!.Contains("fresh stock"));

        var rejected = await admin.PostAsJsonAsync($"/api/apps/whatsapp/records/{id}/reject", new { text = "Add the price, please." });
        Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);
        rec = await (await m.GetAsync($"/api/apps/whatsapp/records/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", rec.GetProperty("status").GetString());
        Assert.Contains("Add the price", rec.GetProperty("data").GetProperty("_rejected").GetProperty("note").GetString());

        // a social post: scheduling it needs approval; approving does it in the admin's name
        var post = await m.PostAsJsonAsync("/api/apps/social-marketing/post", new { parentId = (int?)null, fields = new { title = "Puppy week", content = "Puppy food for growing dogs", platform = "Instagram" } });
        post.EnsureSuccessStatusCode();
        var postId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await m.PostAsJsonAsync($"/api/apps/social-marketing/records/{postId}/status", new { status = "Scheduled" })).EnsureSuccessStatusCode();
        rec = await (await m.GetAsync($"/api/apps/social-marketing/records/{postId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Awaiting approval", rec.GetProperty("status").GetString());
        (await admin.PostAsJsonAsync($"/api/apps/social-marketing/records/{postId}/approve", new { })).EnsureSuccessStatusCode();
        rec = await (await admin.GetAsync($"/api/apps/social-marketing/records/{postId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scheduled", rec.GetProperty("status").GetString());

        // an admin's own scheduling goes straight through
        var own = await admin.PostAsJsonAsync("/api/apps/social-marketing/post", new { parentId = (int?)null, fields = new { title = "Cat week", content = "Cat food", platform = "Facebook" } });
        var ownId = (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync($"/api/apps/social-marketing/records/{ownId}/status", new { status = "Scheduled" })).EnsureSuccessStatusCode();
        rec = await (await admin.GetAsync($"/api/apps/social-marketing/records/{ownId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scheduled", rec.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_marketers_change_to_a_built_in_campaign_is_a_proposal_until_approved()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin), ("k9marketer", RoleNames.Marketer));
        var m = await api.LoginAsync("k9marketer", K9Seed.Company);
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        var list = await (await m.GetAsync("/api/campaigns")).Content.ReadFromJsonAsync<JsonElement>();
        var unpaid = list.EnumerateArray().Single(c => c.GetProperty("type").GetString() == CampaignTypes.UnpaidOrder);
        var id = unpaid.GetProperty("id").GetInt32();
        var body = new { enabled = true, sendWhatsApp = true, sendEmail = false, runTime = "10:00", everyNDays = 1, startDate = (string?)null, endDate = (string?)null,
            cooldownDays = 3, maxRecipients = 50, thresholdDays = 7, fallbackRepurchaseDays = 30 };

        var proposed = await (await m.PutAsJsonAsync($"/api/campaigns/{id}", body)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(proposed.GetProperty("enabled").GetBoolean());                       // not on yet
        Assert.True(proposed.GetProperty("pending").GetProperty("enabled").GetBoolean());

        var approved = await (await admin.PostAsJsonAsync($"/api/campaigns/{id}/approve-change", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(approved.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, approved.GetProperty("pending").ValueKind);

        // leave it as it was for other tests
        (await admin.PutAsJsonAsync($"/api/campaigns/{id}", body with { enabled = false })).EnsureSuccessStatusCode();
    }
}
