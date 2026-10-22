namespace Inventory.Api.Infrastructure;

/// <summary>
/// Runs work for one business without a signed-in user: the public site pages are addressed by company key in the URL, so each request opens a
/// scope that works for that company (the same mechanism the campaign scheduler uses).
/// </summary>
public sealed class CompanyScopes(IServiceScopeFactory scopes)
{
    public async Task<T> RunAsync<T>(string companyKey, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestCompany>().BackgroundKey = companyKey;
        return await work(scope.ServiceProvider);
    }
}
