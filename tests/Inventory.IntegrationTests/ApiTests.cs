using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Inventory.Api.Controllers;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MySql;

namespace Inventory.IntegrationTests;

/// <summary>The real ASP.NET Core app (real Identity, cookies, middleware, migrations) on a real MySQL.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4").WithUsername("root").WithPassword("test-pw-only").Build();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public const string Password = "Passw0rd!2026";

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        // Program reads configuration while building, so environment variables are the reliable way to inject it.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _mysql.GetConnectionString());
        Environment.SetEnvironmentVariable("IdentitySchema", $"t_id_{tag}");
        Environment.SetEnvironmentVariable("Companies__0__Schema", $"t_chewy_{tag}");
        Environment.SetEnvironmentVariable("Companies__1__Schema", $"t_candid_{tag}");
        Environment.SetEnvironmentVariable("Setup__Token", "test-setup-token");
        Environment.SetEnvironmentVariable("RateLimit__LoginPerMinute", "1000");   // production default is 10/min/IP
        Factory = new WebApplicationFactory<Program>();
        _ = Factory.Server;   // starts the host, which creates schemas and applies migrations
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _mysql.DisposeAsync();
    }

    public HttpClient Client(bool csrf = true)
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        if (csrf) c.DefaultRequestHeaders.Add("X-Requested-With", "inventory-ui");
        return c;
    }

    public async Task<HttpClient> LoginAsync(string username, string company = "chewypets")
    {
        var c = Client();
        var r = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, Password, company));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        async Task Add(string name, string role, string hash, bool mustChange = false, bool active = true)
        {
            var u = new AppUser { UserName = name, FullName = name.ToUpperInvariant(), PasswordHash = hash, IsActive = active, MustChangePassword = mustChange, CompanyAccess = "chewypets,candid" };
            var res = await users.CreateAsync(u);
            Assert.True(res.Succeeded, string.Join(";", res.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(u, role);
        }
        var legacy = LegacyAwarePasswordHasher.MakeLegacyHash(Password);   // exactly what the desktop app stored
        await Add("admin", RoleNames.Admin, legacy);
        await Add("clerk", RoleNames.Clerk, legacy);
        await Add("lockme", RoleNames.Clerk, legacy);
        await Add("newbie", RoleNames.Clerk, legacy, mustChange: true);
        await Add("disabled", RoleNames.Clerk, legacy, active: false);
        await Add("placeholder", RoleNames.Clerk, "SETUP_REQUIRED", mustChange: true);   // the desktop seed users

        var registry = scope.ServiceProvider.GetRequiredService<CompanyRegistry>();
        await using var db = registry.CreateFor(registry.Find("chewypets")!);
        var cat = new Domain.Entities.Category { Name = "Dog Food" };
        var wh = new Domain.Entities.Warehouse { Name = "Main" };
        db.AddRange(cat, wh);
        await db.SaveChangesAsync();
        var p = new Domain.Entities.Product { Sku = "SKU-1", Name = "Adult Dog Food", CategoryId = cat.Id, CostPrice = 8500, PriceRetail = 11500, PriceWholesaler = 11000, PriceDistributor = 10500, SellingPrice = 11500, ReorderLevel = 5 };
        var c = new Domain.Entities.Customer { Name = "PetMart", CustomerType = "Retailer" };
        db.AddRange(p, c);
        await db.SaveChangesAsync();
        db.StockBatches.Add(new Domain.Entities.StockBatch { ProductId = p.Id, WarehouseId = wh.Id, BatchNumber = "B1", QuantityOnHand = 20 });
        await db.SaveChangesAsync();
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

[Collection("api")]
public class AuthTests(ApiFixture api)
{
    [Fact]
    public async Task Health_is_public_and_reveals_nothing_else()
    {
        var r = await api.Client(csrf: false).GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"status\":\"healthy\"}", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_user_with_a_desktop_pbkdf2_hash_can_log_in_and_is_rehashed()
    {
        var c = api.Client();
        var r = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", ApiFixture.Password, "chewypets"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var me = await r.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(("ADMIN", "chewypets"), (me!.Role, me.Company));
        Assert.Equal(2, me.Companies.Count);

        using var scope = api.Factory.Services.CreateScope();
        var u = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("admin");
        Assert.DoesNotContain("pbkdf2$", u!.PasswordHash);   // upgraded to Identity's format on first success
        Assert.Equal(HttpStatusCode.OK, (await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", ApiFixture.Password, "candid"))).StatusCode);   // still works after rehash
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_message()
    {
        var a = await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("clerk", "nope-nope-1", "chewypets"));
        var b = await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("ghost", "nope-nope-1", "chewypets"));
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
        Assert.Equal((await a.Content.ReadFromJsonAsync<ProblemDetails>())!.Title, (await b.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var c = api.Client();
        for (var i = 0; i < 5; i++)
            await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("lockme", "wrong-pass-1", "chewypets"));
        var r = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("lockme", ApiFixture.Password, "chewypets"));
        Assert.Equal((HttpStatusCode)423, r.StatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("placeholder")]   // the desktop let anyone into these to set a password; on the web an admin must issue one
    public async Task Disabled_and_placeholder_accounts_cannot_log_in(string user)
    {
        var r = await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "anything-1234", "chewypets"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Unknown_company_is_refused()
    {
        var r = await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("clerk", ApiFixture.Password, "nonexistent"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task State_changing_calls_without_the_csrf_header_are_rejected()
    {
        var r = await api.Client(csrf: false).PostAsJsonAsync("/api/auth/login", new LoginRequest("clerk", ApiFixture.Password, "chewypets"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task A_must_change_password_account_can_do_nothing_until_it_changes_it()
    {
        var c = await api.LoginAsync("newbie");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/me")).StatusCode);

        var weak = await c.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(ApiFixture.Password, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var ok = await c.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(ApiFixture.Password, "Br4ndNewPass9"));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task First_admin_setup_is_closed_once_an_admin_exists()
    {
        var r = await api.Client().PostAsJsonAsync("/api/setup/first-admin", new FirstAdminRequest("test-setup-token", "Eve", "eve", "Sup3rSecret9"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }
}

[Collection("api")]
public class AuthorizationTests(ApiFixture api)
{
    public static TheoryData<string, string> AdminOnly => new()
    {
        { "GET", "/api/customers" }, { "GET", "/api/suppliers" }, { "GET", "/api/purchases" }, { "GET", "/api/finance/summary" },
        { "GET", "/api/finance/income?year=2026" }, { "GET", "/api/finance/ledger" }, { "GET", "/api/users" }, { "GET", "/api/audit" },
        { "POST", "/api/purchases" }, { "POST", "/api/suppliers" }, { "POST", "/api/stock/adjustments" }, { "POST", "/api/sales/1/void" },
        { "DELETE", "/api/products/1" }, { "PUT", "/api/products/1" }, { "POST", "/api/customers/1/payments" }, { "POST", "/api/users" },
    };

    [Theory, MemberData(nameof(AdminOnly))]
    public async Task Clerks_get_403_on_every_admin_endpoint(string method, string url)
    {
        var clerk = await api.LoginAsync("clerk");
        var r = await clerk.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "GET" ? null : JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Theory, MemberData(nameof(AdminOnly))]
    public async Task Anonymous_callers_get_401_everywhere(string method, string url)
    {
        var r = await api.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "GET" ? null : JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Admin_can_reach_admin_endpoints()
    {
        var admin = await api.LoginAsync("admin");
        foreach (var url in new[] { "/api/customers", "/api/suppliers", "/api/purchases", "/api/finance/summary", "/api/users", "/api/audit" })
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
    }

    [Fact]
    public void Every_controller_action_declares_its_authorization_explicitly()
    {
        var actions = typeof(AuthController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name.StartsWith("Http")))
                .Select(m => (Type: t, Method: m)));
        var missing = actions.Where(a =>
            !(a.Method.GetCustomAttributes<AuthorizeAttribute>().Any() || a.Method.GetCustomAttributes<AllowAnonymousAttribute>().Any()
              || a.Type.GetCustomAttributes<AuthorizeAttribute>().Any() || a.Type.GetCustomAttributes<AllowAnonymousAttribute>().Any()))
            .Select(a => $"{a.Type.Name}.{a.Method.Name}").ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Clerks_never_receive_cost_or_profit_figures()
    {
        var clerk = await api.LoginAsync("clerk");
        var products = JsonDocument.Parse(await clerk.GetStringAsync("/api/products")).RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Null, products[0].GetProperty("costPrice").ValueKind);

        var dash = JsonDocument.Parse(await clerk.GetStringAsync("/api/dashboard")).RootElement;
        Assert.Equal(JsonValueKind.Null, dash.GetProperty("grossProfitThisMonth").ValueKind);
        Assert.Equal(JsonValueKind.Null, dash.GetProperty("reorder").ValueKind);

        var admin = await api.LoginAsync("admin");
        var aProducts = JsonDocument.Parse(await admin.GetStringAsync("/api/products")).RootElement.GetProperty("items");
        Assert.Equal(8500m, aProducts[0].GetProperty("costPrice").GetDecimal());
    }
}

[Collection("api")]
public class SalesApiTests(ApiFixture api)
{
    [Fact]
    public async Task A_clerk_can_sell_over_http_and_a_retry_with_the_same_key_does_not_sell_twice()
    {
        var clerk = await api.LoginAsync("clerk");
        var products = JsonDocument.Parse(await clerk.GetStringAsync("/api/products")).RootElement.GetProperty("items");
        var productId = products[0].GetProperty("id").GetInt32();
        var customers = JsonDocument.Parse(await clerk.GetStringAsync("/api/customers/lookup")).RootElement;
        var customerId = customers[0].GetProperty("id").GetInt32();
        var warehouses = JsonDocument.Parse(await clerk.GetStringAsync("/api/warehouses")).RootElement;
        var warehouseId = warehouses[0].GetProperty("id").GetInt32();

        var body = new { customerId, warehouseId, priceTier = "Retailer", paymentMethod = "Cash", vatRate = 7.5, paidNow = 5000, lines = new[] { new { productId, quantity = 2, unitPrice = 11500 } } };
        HttpRequestMessage Req() { var m = new HttpRequestMessage(HttpMethod.Post, "/api/sales") { Content = JsonContent.Create(body) }; m.Headers.Add("Idempotency-Key", "http-key-1"); return m; }

        var first = await clerk.SendAsync(Req());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await clerk.SendAsync(Req());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadFromJsonAsync<JsonElement>() is var f ? f.GetProperty("invoiceId").GetInt32() : 0,
                     (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoiceId").GetInt32());

        var list = JsonDocument.Parse(await clerk.GetStringAsync("/api/sales")).RootElement;
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("items")[0].GetProperty("estProfit").ValueKind);   // clerk: no profit
    }

    [Fact]
    public async Task Overselling_returns_409_with_the_shortfall()
    {
        var clerk = await api.LoginAsync("clerk");
        var productId = JsonDocument.Parse(await clerk.GetStringAsync("/api/products")).RootElement.GetProperty("items")[0].GetProperty("id").GetInt32();
        var customerId = JsonDocument.Parse(await clerk.GetStringAsync("/api/customers/lookup")).RootElement[0].GetProperty("id").GetInt32();
        var warehouseId = JsonDocument.Parse(await clerk.GetStringAsync("/api/warehouses")).RootElement[0].GetProperty("id").GetInt32();
        var r = await clerk.PostAsJsonAsync("/api/sales", new { customerId, warehouseId, priceTier = "Retailer", paymentMethod = "Cash", vatRate = 0, paidNow = 0, lines = new[] { new { productId, quantity = 99999, unitPrice = 1 } } });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("shortfalls", body);
        Assert.DoesNotContain("Exception", body);   // no internals leak into error responses
    }

    [Fact]
    public async Task Invalid_input_returns_400_with_field_errors()
    {
        var clerk = await api.LoginAsync("clerk");
        var r = await clerk.PostAsJsonAsync("/api/sales", new { customerId = 0, warehouseId = 0, priceTier = "Bogus", paymentMethod = "Cash", lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

[Collection("api")]
public class UserAdminTests(ApiFixture api)
{
    private static async Task<int> FindId(HttpClient admin, string username)
    {
        var rows = JsonDocument.Parse(await admin.GetStringAsync("/api/users")).RootElement;
        return rows.EnumerateArray().First(r => r.GetProperty("username").GetString() == username).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Admin_creates_a_user_who_must_change_the_temporary_password_before_anything_else()
    {
        var admin = await api.LoginAsync("admin");
        var r = await admin.PostAsJsonAsync("/api/users", new { fullName = "Tunde Bello", username = "tunde", password = "Temp12345pw", role = "CLERK", companies = new[] { "chewypets" } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var c = api.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("tunde", "Temp12345pw", "chewypets"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/products")).StatusCode);
        // Only opened for the business the admin allowed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("tunde", "Temp12345pw", "candid"))).StatusCode);
    }

    [Fact]
    public async Task Weak_first_passwords_are_refused()
    {
        var admin = await api.LoginAsync("admin");
        var r = await admin.PostAsJsonAsync("/api/users", new { fullName = "Weak One", username = "weak1", password = "short", role = "CLERK", companies = new[] { "chewypets" } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Admin_reset_unlocks_a_locked_account_and_forces_a_new_password()
    {
        var admin = await api.LoginAsync("admin");
        await admin.PostAsJsonAsync("/api/users", new { fullName = "Lock Me Too", username = "locked2", password = "Temp12345pw", role = "CLERK", companies = new[] { "chewypets" } });
        var c = api.Client();
        for (var i = 0; i < 5; i++) await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("locked2", "wrong-pass-1", "chewypets"));
        Assert.Equal((HttpStatusCode)423, (await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("locked2", "Temp12345pw", "chewypets"))).StatusCode);

        var id = await FindId(admin, "locked2");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/users/{id}/reset-password", new { newPassword = "Fresh98765pw" })).StatusCode);

        var after = await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("locked2", "Fresh98765pw", "chewypets"));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.True((await after.Content.ReadFromJsonAsync<MeResponse>())!.MustChangePassword);
    }

    [Fact]
    public async Task A_disabled_user_is_signed_out_and_cannot_sign_back_in_and_an_admin_cannot_lock_themselves_out()
    {
        var admin = await api.LoginAsync("admin");
        await admin.PostAsJsonAsync("/api/users", new { fullName = "Short Stay", username = "shortstay", password = "Temp12345pw", role = "CLERK", companies = new[] { "chewypets" } });
        var id = await FindId(admin, "shortstay");
        var session = api.Client();
        await session.PostAsJsonAsync("/api/auth/login", new LoginRequest("shortstay", "Temp12345pw", "chewypets"));

        var off = await admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Short Stay", role = "CLERK", companies = new[] { "chewypets" }, isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);   // security stamp changed: existing session dies
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest("shortstay", "Temp12345pw", "chewypets"))).StatusCode);

        var myId = await FindId(admin, "admin");
        var self = await admin.PutAsJsonAsync($"/api/users/{myId}", new { fullName = "ADMIN", role = "CLERK", companies = new[] { "chewypets", "candid" }, isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
    }
}
