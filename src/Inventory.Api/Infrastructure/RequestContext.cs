using System.Security.Claims;
using Inventory.Application.Abstractions;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;

namespace Inventory.Api.Infrastructure;

public static class AppClaims
{
    public const string Company = "company";
    public const string FullName = "full_name";
}

/// <summary>The company the signed-in user opened. Comes from the auth cookie, never from a request parameter.</summary>
public sealed class RequestCompany(IHttpContextAccessor http, CompanyRegistry registry) : ICompanyContext
{
    /// <summary>Set by the payment webhook only, from a reference this server generated and the processor confirmed. Signed-in requests never use it.</summary>
    public const string OverrideItem = "company-override";

    public CompanyInfo Info => registry.Find(http.HttpContext?.Items[OverrideItem] as string ?? http.HttpContext?.User.FindFirstValue(AppClaims.Company))
        ?? throw new InvalidOperationException("No company selected for this request.");
    public string Key => Info.Key;
    public string DocumentPrefix => Info.DocumentPrefix;
    public string LegalName => string.IsNullOrWhiteSpace(Info.LegalName) ? Info.DisplayName : Info.LegalName;
    public bool HasPriceLists => Info.HasPriceLists;
    public bool BuysGoods => Info.BuysGoods;
    public int DefaultWarehouseId => Info.DefaultWarehouseId;
    public IReadOnlyList<(string Bank, string AccountName, string AccountNumber)> DefaultBanks =>
        (Info.Banks ?? []).Select(b => (b.Bank, b.AccountName, b.AccountNumber)).ToList();
}

public sealed class HttpCurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    public CurrentUser? User
    {
        get
        {
            var p = http.HttpContext?.User;
            if (p?.Identity?.IsAuthenticated != true) return null;
            var id = p.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(id, out var n)
                ? new CurrentUser(n, p.FindFirstValue(AppClaims.FullName) ?? p.Identity.Name ?? "", p.FindFirstValue(ClaimTypes.Role) ?? "")
                : null;
        }
    }
}
