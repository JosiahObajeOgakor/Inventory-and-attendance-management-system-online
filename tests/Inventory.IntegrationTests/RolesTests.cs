using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Inventory.Api.Infrastructure;
using Inventory.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.IntegrationTests;

/// <summary>Every endpoint names a permission, and the CEO's ticks under People &amp; access → Roles decide who gets through.</summary>
public class EndpointPermissionTests
{
    /// <summary>Signed-in endpoints any user needs whatever their role (their own session).</summary>
    private static readonly string[] AnyUser = ["AuthController.Logout", "AuthController.Me", "AuthController.ChangePassword", "AuthController.SwitchCompany", "ApprovalsController.Mine"];

    [Fact]
    public void Every_endpoint_requires_a_permission_or_is_deliberately_public()
    {
        var open = new List<string>();
        foreach (var type in typeof(Perm).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var classAttrs = type.GetCustomAttributes<AuthorizeAttribute>(true).ToList();
            var classAnon = type.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name.StartsWith("Http"))))
            {
                if (classAnon || m.GetCustomAttributes<AllowAnonymousAttribute>().Any()) continue;
                var policies = classAttrs.Concat(m.GetCustomAttributes<AuthorizeAttribute>()).Select(a => a.Policy).Where(p => p is not null).ToList();
                if (policies.Any(p => p!.StartsWith(Perm.Prefix))) continue;
                if (AnyUser.Contains($"{type.Name}.{m.Name}")) continue;
                // the apps endpoint checks each app's own permission in code
                if (type.Name == "AppsRecordsController") continue;
                open.Add($"{type.Name}.{m.Name}");
            }
        }
        Assert.Empty(open);
    }

    [Fact]
    public void Every_permission_a_policy_names_exists() =>
        Assert.All(typeof(Perm).GetFields().Where(f => f.IsLiteral && f.Name != nameof(Perm.Prefix)), f => Assert.True(Permissions.IsKnown(((string)f.GetRawConstantValue()!)[Perm.Prefix.Length..])));
}

[Collection("api")]
public class RolesTests(ApiFixture api)
{
    [Fact]
    public async Task The_CEO_ticks_a_permission_on_a_role_and_people_in_it_get_through_then_lose_it_again()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin), ("k9marketer2", RoleNames.Marketer));
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        var roles = await (await admin.GetAsync("/api/roles")).Content.ReadFromJsonAsync<JsonElement>();
        var marketer = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == RoleNames.Marketer);
        var before = marketer.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray();
        Assert.Contains(Permissions.CampaignsManage, before);
        Assert.DoesNotContain(Permissions.SalesView, before);

        var m = await api.LoginAsync("k9marketer2", K9Seed.Company);
        Assert.Equal(HttpStatusCode.Forbidden, (await m.GetAsync("/api/sales")).StatusCode);
        Assert.Contains(Permissions.CampaignsManage, (await (await m.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        try
        {
            (await admin.PutAsJsonAsync($"/api/roles/{RoleNames.Marketer}/permissions", new { permissions = before.Append(Permissions.SalesView).ToArray() })).EnsureSuccessStatusCode();
            var again = await api.LoginAsync("k9marketer2", K9Seed.Company);
            Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/sales")).StatusCode);
        }
        finally
        {
            (await admin.PutAsJsonAsync($"/api/roles/{RoleNames.Marketer}/permissions", new { permissions = before })).EnsureSuccessStatusCode();
        }
        var after = await api.LoginAsync("k9marketer2", K9Seed.Company);
        Assert.Equal(HttpStatusCode.Forbidden, (await after.GetAsync("/api/sales")).StatusCode);
    }

    [Fact]
    public async Task The_CEO_role_always_keeps_everything_and_a_new_role_can_be_copied_and_removed()
    {
        await K9Seed.EnsureAsync(api, ("k9admin", RoleNames.Admin));
        var admin = await api.LoginAsync("k9admin", K9Seed.Company);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/roles/{RoleNames.Admin}/permissions", new { permissions = new[] { Permissions.SalesView } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/roles/{RoleNames.Clerk}/permissions", new { permissions = new[] { "made.up" } })).StatusCode);

        (await admin.PostAsJsonAsync("/api/roles", new { name = "Shop attendant", copyFrom = RoleNames.Clerk })).EnsureSuccessStatusCode();
        var roles = await (await admin.GetAsync("/api/roles")).Content.ReadFromJsonAsync<JsonElement>();
        var copy = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Shop attendant");
        var clerk = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == RoleNames.Clerk);
        Assert.Equal(clerk.GetProperty("permissions").GetRawText(), copy.GetProperty("permissions").GetRawText());
        Assert.False(copy.GetProperty("builtIn").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/roles/Shop%20attendant")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/roles/{RoleNames.Clerk}")).StatusCode);
    }

    [Fact]
    public async Task A_clerk_can_not_edit_roles()
    {
        await K9Seed.EnsureAsync(api, ("k9clerk", RoleNames.Clerk));
        var clerk = await api.LoginAsync("k9clerk", K9Seed.Company);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/roles")).StatusCode);
    }
}
