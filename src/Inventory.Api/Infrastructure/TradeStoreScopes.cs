using Inventory.Application.Partners;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Api.Infrastructure;

/// <summary>A fresh DI scope pinned to one store (the same way the schedulers work), so its services read and write that store's schema.</summary>
public sealed class TradeStoreScopes(IServiceProvider services, CompanyRegistry registry, IConfiguration config) : ICompanyScopes
{
    public IReadOnlyList<string> TradeStores =>
        (config["Partners:Stores"] is { Length: > 0 } s ? s : "chewypets,candid").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(k => registry.Find(k)?.Key).OfType<string>().Distinct().ToList();

    public string DisplayName(string key) => registry.Find(key)?.DisplayName ?? key;

    public async Task<T> InAsync<T>(string key, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestCompany>().BackgroundKey = registry.Find(key)?.Key ?? throw new InvalidOperationException($"Unknown store {key}.");
        return await work(scope.ServiceProvider);
    }
}

