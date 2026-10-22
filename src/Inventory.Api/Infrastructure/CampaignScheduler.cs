using Inventory.Application.Campaigns;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// Wakes every minute and runs whichever campaigns an admin has switched on and scheduled for now, for each company. All "is it due" logic is in
/// <see cref="CampaignSchedule"/>; the per-day claim in <see cref="CampaignService.RunDueAsync"/> makes a restart or a second server harmless.
/// </summary>
public sealed class CampaignScheduler(IServiceProvider services, CompanyRegistry registry, IOptions<CampaignOptions> options, ILogger<CampaignScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.SchedulerEnabled) { log.LogInformation("Campaign scheduler is switched off (Campaigns:SchedulerEnabled=false)."); return; }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stop);          // let start-up migrations finish first
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                foreach (var company in registry.All.Where(c => c.HasNewFeatures || c.HasMarketing))
                {
                    try
                    {
                        using var scope = services.CreateScope();
                        scope.ServiceProvider.GetRequiredService<RequestCompany>().BackgroundKey = company.Key;
                        var ran = await scope.ServiceProvider.GetRequiredService<CampaignService>().RunDueAsync(DateTime.UtcNow, stop);
                        foreach (var (type, result) in ran) log.LogInformation("Campaign {Type} for {Company}: {Note}", type, company.Key, result.Note);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Campaign tick failed for {Company}", company.Key); }
                }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
