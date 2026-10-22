using Inventory.Application.AppsEngine;
using Inventory.Application.Campaigns;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// Sends the queued email, SMS and WhatsApp broadcasts a few at a time (never in quiet hours), and once an hour checks every Marketing Automation rule that is
/// switched on. Follows the Campaigns switch: <c>Campaigns:SchedulerEnabled=false</c> stops it too.
/// </summary>
public sealed class BroadcastWorker(IServiceProvider services, CompanyRegistry registry, IOptions<CampaignOptions> options, ILogger<BroadcastWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.SchedulerEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stop);          // let start-up migrations finish first
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            var tick = 0;
            do
            {
                var rules = tick++ % 120 == 0;                          // every hour
                foreach (var company in registry.All.Where(c => c.HasNewFeatures))
                {
                    try
                    {
                        using var scope = services.CreateScope();
                        scope.ServiceProvider.GetRequiredService<RequestCompany>().BackgroundKey = company.Key;
                        if (rules) { var queued = await scope.ServiceProvider.GetRequiredService<AutomationService>().RunAllAsync(stop); if (queued > 0) log.LogInformation("Automation {Company}: {Count} message(s) queued", company.Key, queued); }
                        var sent = await scope.ServiceProvider.GetRequiredService<BroadcastDispatcher>().DispatchAsync(25, stop);
                        if (sent > 0) log.LogInformation("Broadcast {Company}: {Count} message(s) handled", company.Key, sent);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Broadcast tick failed for {Company}", company.Key); }
                }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}