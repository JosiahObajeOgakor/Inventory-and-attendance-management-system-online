using System.Threading.Channels;
using Inventory.Application.SalesAssistant;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Infrastructure;

/// <summary>Lets the webhook wake the inbox worker the moment a message is stored, instead of waiting for its next poll.</summary>
public static class InboxSignal
{
    private static readonly Channel<bool> Ch = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public static void Wake() => Ch.Writer.TryWrite(true);
    public static async Task WaitAsync(TimeSpan max, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(max);
        try { await Ch.Reader.ReadAsync(cts.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* poll timeout */ }
    }
}

/// <summary>
/// Answers stored WhatsApp messages for every company: claims due messages (one per customer, oldest first), answers them in parallel
/// up to <see cref="InboxOptions.Concurrency"/>, and polls every few seconds as a safety net for retries and missed wake-ups.
/// Once an hour it deletes old answered messages. Safe to run on several servers at once (claims use SKIP LOCKED + leases).
/// </summary>
public sealed class WhatsAppInboxWorker(IServiceScopeFactory scopes, CompanyRegistry registry, IOptions<InboxOptions> options, ILogger<WhatsAppInboxWorker> log) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    private DateTime _nextPurge = DateTime.UtcNow.AddMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var didWork = false;
            foreach (var company in registry.All)
            {
                try { didWork |= await RunOnceAsync(company.Key, stop); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Inbox pass failed for {Company}", company.Key); }
            }
            if (DateTime.UtcNow >= _nextPurge) { _nextPurge = DateTime.UtcNow.AddHours(1); await PurgeAllAsync(stop); }
            if (!didWork) await InboxSignal.WaitAsync(Poll, stop);
        }
    }

    /// <summary>One claim-and-answer round for one company. True when it answered anything.</summary>
    public async Task<bool> RunOnceAsync(string companyKey, CancellationToken ct)
    {
        List<long> ids;
        RequestCompany.Background.Value = companyKey;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            ids = await scope.ServiceProvider.GetRequiredService<WhatsAppInbox>().ClaimAsync(Math.Max(1, options.Value.Concurrency), ct);
        }
        finally { RequestCompany.Background.Value = null; }
        if (ids.Count == 0) return false;

        // Each message gets its own scope (its own DbContext) so they can run side by side.
        await Task.WhenAll(ids.Select(id => Task.Run(async () =>
        {
            RequestCompany.Background.Value = companyKey;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WhatsAppInboxProcessor>().ProcessAsync(id, ct);
        }, ct)));
        return true;
    }

    private async Task PurgeAllAsync(CancellationToken ct)
    {
        foreach (var company in registry.All)
        {
            RequestCompany.Background.Value = company.Key;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var n = await scope.ServiceProvider.GetRequiredService<WhatsAppInbox>().PurgeAsync(ct);
                if (n > 0) log.LogInformation("Deleted {Count} old inbox message(s) for {Company}", n, company.Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Inbox purge failed for {Company}", company.Key); }
            finally { RequestCompany.Background.Value = null; }
        }
    }
}
