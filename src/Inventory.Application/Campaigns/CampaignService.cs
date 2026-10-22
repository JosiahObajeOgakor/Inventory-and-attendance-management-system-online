using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Campaigns;

public sealed record CampaignUpdate(bool Enabled, bool SendWhatsApp, bool SendEmail, string RunTime, int EveryNDays, DateOnly? StartDate, DateOnly? EndDate,
    int CooldownDays, int MaxRecipients, int ThresholdDays, int FallbackRepurchaseDays);

public sealed record CampaignDto(int Id, string Type, string Name, string Description, bool Enabled, bool SendWhatsApp, bool SendEmail, string RunTime, int EveryNDays,
    DateOnly? StartDate, DateOnly? EndDate, int CooldownDays, int MaxRecipients, int ThresholdDays, int FallbackRepurchaseDays, DateOnly? LastRunOn, DateTime? LastRunAt,
    int LastRunSent, string? LastRunNote, bool ForAdmin);

public sealed record CampaignMessageDto(long Id, string Campaign, string Customer, string Recipient, string Channel, string Language, string Status, string? Detail, string? Mode, string Text, DateTime At);
public sealed record ConsentStats(int OptedIn, int OptedOut, int Unknown);

/// <summary>Admin-facing operations on campaigns and the scheduler's "run whatever is due" entry point.</summary>
public sealed class CampaignService(IBusinessDbContext db, CampaignRunner runner, IClock clock, ILogger<CampaignService> log)
{
    private static readonly (string Type, string Name, string Description, int Minute, int Every, int Threshold, bool Whatsapp, bool Email)[] Defaults =
    [
        (CampaignTypes.Reorder, "Reorder reminders", "Reminds customers who bought a product some time ago that they may be running out, using their own buying rhythm and today's stock. Warns when our stock is low.", 7 * 60 + 30, 1, 7, true, false),
        (CampaignTypes.UnpaidOrder, "Unpaid orders & overdue balances", "Nudges customers who started an order and never paid (offering a fresh payment account), and those with a balance past its due date.", 9 * 60, 1, 7, true, false),
        (CampaignTypes.ChatDropOff, "Chat drop-offs", "Follows up people who picked products in the WhatsApp or website chat and left before ordering, reminding them of exactly what they picked if it is still in stock. Looks back the number of days below; each person is reminded once.", 11 * 60, 1, 3, true, false),
        (CampaignTypes.Promo, "Offers from stock", "Tells a customer about a product that is in stock and worth moving, matched to what they bought before. Never invents a discount.", 10 * 60, 7, 7, true, false),
        (CampaignTypes.AdminRestock, "Restock alerts to the admin", "A morning digest of products that will run out within the number of days below, with the daily rate, the date, a suggested order and your biggest buyer. Sent only on days something is at risk.", 6 * 60 + 30, 1, 7, true, true),
    ];

    private static string Describe(string type) => Defaults.First(d => d.Type == type).Description;

    public async Task<List<CampaignDto>> ListAsync(CancellationToken ct)
    {
        await EnsureDefaultsAsync(ct);
        var rows = await db.Campaigns.AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(c => Array.IndexOf(CampaignTypes.All, c.Type)).Select(ToDto).ToList();
    }

    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        var have = await db.Campaigns.Select(c => c.Type).ToListAsync(ct);
        var missing = Defaults.Where(d => !have.Contains(d.Type)).ToList();
        foreach (var d in missing)
            db.Campaigns.Add(new Campaign { Type = d.Type, Name = d.Name, Enabled = false, SendWhatsApp = d.Whatsapp, SendEmail = d.Email, RunMinute = d.Minute, EveryNDays = d.Every, ThresholdDays = d.Threshold, UpdatedAt = clock.UtcNow });
        if (missing.Count > 0) await db.SaveChangesAsync(ct);
    }

    public async Task<CampaignDto> UpdateAsync(int id, CampaignUpdate u, string by, CancellationToken ct)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Campaign");
        var minute = ParseTime(u.RunTime);
        var admin = c.Type == CampaignTypes.AdminRestock;
        if (!admin && CampaignSchedule.IsQuietHour(minute)) throw new BusinessRuleException("Customers can only be messaged between 07:00 and 20:30.");
        if (u.EveryNDays is < 1 or > 60) throw new BusinessRuleException("Run every 1 to 60 days.");
        if (u.CooldownDays is < 0 or > 60) throw new BusinessRuleException("Cooldown must be 0 to 60 days.");
        if (u.MaxRecipients is < 1 or > 1000) throw new BusinessRuleException("Most customers per run must be 1 to 1000.");
        if (u.ThresholdDays is < 1 or > 60) throw new BusinessRuleException("'About to run out' must be 1 to 60 days.");
        if (u.FallbackRepurchaseDays is < 3 or > 120) throw new BusinessRuleException("Default repurchase interval must be 3 to 120 days.");
        if (u.StartDate is { } s && u.EndDate is { } e && e < s) throw new BusinessRuleException("The end date is before the start date.");
        if (!u.SendWhatsApp && !u.SendEmail) throw new BusinessRuleException("Pick at least one channel.");

        c.Enabled = u.Enabled; c.SendWhatsApp = u.SendWhatsApp; c.SendEmail = u.SendEmail; c.RunMinute = minute; c.EveryNDays = u.EveryNDays; c.StartDate = u.StartDate; c.EndDate = u.EndDate;
        c.CooldownDays = u.CooldownDays; c.MaxRecipients = u.MaxRecipients; c.ThresholdDays = u.ThresholdDays; c.FallbackRepurchaseDays = u.FallbackRepurchaseDays;
        c.UpdatedAt = clock.UtcNow; c.UpdatedBy = by;
        db.AuditLogs.Add(new AuditLog { UserName = by, Action = "CAMPAIGN_UPDATED", Entity = "Campaign", EntityId = c.Id.ToString(), At = clock.UtcNow, Detail = $"{c.Name}: {(c.Enabled ? "on" : "off")} at {CampaignSchedule.Describe(c.RunMinute)} every {c.EveryNDays} day(s)" });
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public static int ParseTime(string? hhmm)
    {
        var parts = (hhmm ?? "").Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || h is < 0 or > 23 || m is < 0 or > 59) throw new BusinessRuleException("Time must look like 07:30.");
        return h * 60 + m;
    }

    public async Task<RunResult> PreviewAsync(int id, CancellationToken ct)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Campaign");
        return await runner.RunAsync(c, preview: true, ct);
    }

    /// <summary>Runs now on an admin's say-so. Opt-in, cooldown, quiet hours and the template rule still apply; it does not use up today's scheduled run.</summary>
    public async Task<RunResult> RunNowAsync(int id, string by, CancellationToken ct)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Campaign");
        var result = await runner.RunAsync(c, preview: false, ct);
        c.LastRunAt = clock.UtcNow; c.LastRunSent = result.Sent; c.LastRunNote = $"Manual run by {by}: {result.Note}";
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Called by the scheduler every minute. Each due campaign is claimed with one conditional UPDATE, so it cannot run twice in a day even across restarts or servers.</summary>
    public async Task<List<(string Type, RunResult Result)>> RunDueAsync(DateTime utcNow, CancellationToken ct)
    {
        var ran = new List<(string, RunResult)>();
        var due = (await db.Campaigns.AsNoTracking().ToListAsync(ct)).Where(c => CampaignSchedule.IsDue(c, utcNow)).ToList();
        if (due.Count == 0) return ran;
        var (today, _) = CampaignSchedule.LocalNow(utcNow);
        foreach (var c in due)
        {
            var claimed = await db.Campaigns.Where(x => x.Id == c.Id && (x.LastRunOn == null || x.LastRunOn < today)).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastRunOn, today), ct);
            if (claimed != 1) continue;
            RunResult result;
            try { result = await runner.RunAsync(c, preview: false, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Campaign {Type} failed", c.Type);
                result = new RunResult(0, 0, 0, 0, [], "Failed: " + ex.Message);
            }
            await db.Campaigns.Where(x => x.Id == c.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.LastRunAt, utcNow).SetProperty(x => x.LastRunSent, result.Sent).SetProperty(x => x.LastRunNote, result.Note.Length > 300 ? result.Note[..300] : result.Note), ct);
            ran.Add((c.Type, result));
        }
        return ran;
    }

    public async Task<List<CampaignMessageDto>> LogAsync(int? campaignId, int take, CancellationToken ct)
    {
        var q = from m in db.CampaignMessages.AsNoTracking()
                join c in db.Campaigns.AsNoTracking() on m.CampaignId equals c.Id
                join cu in db.Customers.AsNoTracking() on m.CustomerId equals cu.Id into cus
                from cu in cus.DefaultIfEmpty()
                where campaignId == null || m.CampaignId == campaignId
                orderby m.Id descending
                select new CampaignMessageDto(m.Id, c.Name, cu != null ? cu.Name : "Admin", m.Recipient, m.Channel, m.Language, m.Status, m.Detail, m.Mode, m.Text, m.CreatedAt);
        return await q.Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    public async Task<ConsentStats> StatsAsync(CancellationToken ct)
    {
        var g = await db.Customers.AsNoTracking().GroupBy(c => c.MarketingStatus).Select(x => new { x.Key, N = x.Count() }).ToListAsync(ct);
        int Of(string s) => g.FirstOrDefault(x => x.Key == s)?.N ?? 0;
        return new ConsentStats(Of(MarketingStatuses.OptedIn), Of(MarketingStatuses.OptedOut), Of(MarketingStatuses.Unknown));
    }

    private static CampaignDto ToDto(Campaign c) => new(c.Id, c.Type, c.Name, Describe(c.Type), c.Enabled, c.SendWhatsApp, c.SendEmail, CampaignSchedule.Describe(c.RunMinute), c.EveryNDays,
        c.StartDate, c.EndDate, c.CooldownDays, c.MaxRecipients, c.ThresholdDays, c.FallbackRepurchaseDays, c.LastRunOn, c.LastRunAt, c.LastRunSent, c.LastRunNote, c.Type == CampaignTypes.AdminRestock);
}
