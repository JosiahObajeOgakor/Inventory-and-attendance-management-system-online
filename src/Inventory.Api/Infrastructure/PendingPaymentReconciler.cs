using Inventory.Application.Abstractions;
using Inventory.Application.Payments;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// Safety net for missed or slow webhooks (AlatPay's first retry only comes 30 minutes later). Every few minutes, for every company, it asks the
/// processors about links created in the last day that are still pending, and settles + dispatches the ones that were actually paid.
/// Settling is idempotent, so racing a webhook that arrives at the same moment is harmless.
/// </summary>
public sealed class PendingPaymentReconciler(IServiceScopeFactory scopes, CompanyRegistry registry, ILogger<PendingPaymentReconciler> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stop))
        {
            foreach (var company in registry.All)
            {
                try { await RunForAsync(company.Key, stop); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Payment reconciliation failed for {Company}", company.Key); }
            }
        }
    }

    public async Task RunForAsync(string companyKey, CancellationToken ct)
    {
        RequestCompany.Background.Value = companyKey;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var pay = scope.ServiceProvider.GetRequiredService<PaymentLinkService>();
            if (!pay.Enabled) return;
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var dispatch = scope.ServiceProvider.GetRequiredService<OrderDispatchService>();
            var settled = await pay.SettlePendingAsync(null, clock.UtcNow.AddDays(-1), ct);
            foreach (var result in settled) await dispatch.CompleteAsync(result, ct);
            if (settled.Count > 0) log.LogInformation("Reconciler settled {Count} missed payment(s) for {Company}", settled.Count, companyKey);
        }
        finally { RequestCompany.Background.Value = null; }
    }
}
