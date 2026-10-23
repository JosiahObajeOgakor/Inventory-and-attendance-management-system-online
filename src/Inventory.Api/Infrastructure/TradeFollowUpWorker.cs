using Inventory.Application.Partners;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// Once an hour: follows up trade applications that have gone quiet (3, 7 and 21 days after the last step; the third email is a 24-hour warning) and removes
/// those still untouched 24 hours after the warning. Emails go out between 08:00 and 19:00 Lagos time only; removals happen whenever they are due.
/// </summary>
public sealed class TradeFollowUpWorker(IServiceProvider services, ICompanyScopes stores, IConfiguration config, ILogger<TradeFollowUpWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stop);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    var hour = DateTime.UtcNow.AddHours(1).Hour;
                    if (hour is < 8 or >= 19) continue;       // nobody gets a reminder at night
                    var site = config["Site:PublicUrl"] is { Length: > 0 } u ? u : "https://chewypetsfeeds.com";
                    var (emailed, deleted) = await stores.InAsync(stores.TradeStores[0], sp => sp.GetRequiredService<TradeRegistrationService>().FollowUpAsync(site, stop));
                    if (emailed + deleted > 0) log.LogInformation("Trade applications: {Emailed} follow-up email(s), {Deleted} removed", emailed, deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Trade follow-up run failed"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
