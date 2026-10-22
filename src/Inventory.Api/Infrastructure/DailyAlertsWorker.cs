using Inventory.Application.Alerts;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// Sends each company its daily digest once a morning: what has run out, what is running low, what expires soon, who owes us, who we owe, and
/// which customers are overdue to buy again. Modelled on <see cref="PendingPaymentReconciler"/> — one pass per company, failures logged and
/// swallowed so a mail problem can never disturb the books.
///
/// The hour is business-local (Alerts__HourLocal, default 07:00 Lagos). A digest with nothing in it is not sent, and each company gets at most
/// one a day even if the service restarts.
/// </summary>
public sealed class DailyAlertsWorker(IServiceScopeFactory scopes, CompanyRegistry registry, IConfiguration config, ILogger<DailyAlertsWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan Check = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, DateOnly> _sent = [];

    private int Hour => Math.Clamp(config.GetValue("Alerts:HourLocal", 7), 0, 23);
    private bool Enabled => config.GetValue("Alerts:Enabled", true);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Check);
        while (await timer.WaitForNextTickAsync(stop))
        {
            if (!Enabled) continue;
            // Lagos is UTC+1 all year, the same assumption the rest of the app makes about "the business day".
            var localNow = DateTime.UtcNow.AddHours(1);
            if (localNow.Hour < Hour) continue;
            var today = DateOnly.FromDateTime(localNow);

            foreach (var company in registry.All)
            {
                if (_sent.GetValueOrDefault(company.Key) == today) continue;
                try
                {
                    var outcome = await RunForAsync(company.Key, null, null, stop);
                    _sent[company.Key] = today;
                    log.LogInformation("Daily alerts for {Company}: {Outcome}", company.Key, outcome);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not marked as sent: the next quarter-hour tick tries again.
                    log.LogWarning(ex, "Daily alerts failed for {Company}", company.Key);
                }
            }
        }
    }

    /// <summary>Builds and sends one company's digest. Also used by the "send it to me now" button.</summary>
    public async Task<string> RunForAsync(string companyKey, string? emailTo, string? whatsappTo, CancellationToken ct)
    {
        RequestCompany.Background.Value = companyKey;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var alerts = scope.ServiceProvider.GetRequiredService<BusinessAlertService>();
            return await alerts.SendAsync(emailTo, whatsappTo, ct);
        }
        finally { RequestCompany.Background.Value = null; }
    }
}
