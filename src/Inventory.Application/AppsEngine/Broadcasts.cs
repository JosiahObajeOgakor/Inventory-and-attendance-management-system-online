using System.Net;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Campaigns;
using Inventory.Application.Common;
using Inventory.Application.Messaging;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Application.AppsEngine;

public sealed record Target(int CustomerId, string Name, string? Phone, string? Email);

/// <summary>
/// Who a broadcast or an automation rule can reach, and writing one pending message per person. The rules that protect customers are the same ones the
/// Campaigns screen follows and cannot be switched off here: only customers who opted in, consent checked again at the moment of sending, nothing before
/// 07:00 or after 20:30 Lagos time, WhatsApp free text only inside its 24-hour window (an approved template otherwise), and every email or text carries a
/// working unsubscribe link.
/// </summary>
public sealed class BroadcastQueue(IBusinessDbContext db, IClock clock, IChatSender chat, ICampaignEmailSender email, ISmsSender sms, ICompanyContext company)
{
    public static readonly string[] Audiences =
        ["All opted-in customers", "Opted-in customers who owe me", "Opted-in Gold customers", "Opted-in customers who haven't bought in 90 days"];

    /// <summary>Refuses early, in plain words, when the server can't send on this channel at all.</summary>
    public void EnsureChannel(string channel)
    {
        var ok = channel switch { "Email" => email.IsConfigured, "SMS" => sms.IsConfigured, "WhatsApp" => chat.IsConfigured(company.Key), _ => false };
        if (!ok) throw new BusinessRuleException($"{channel} isn't set up on this server yet, so nothing can be sent. Ask your administrator to add the {channel} provider settings.");
    }

    public static string? AddressFor(string channel, Target t) => channel == "Email"
        ? (string.IsNullOrWhiteSpace(t.Email) ? null : t.Email.Trim())
        : string.IsNullOrWhiteSpace(t.Phone) ? null : CampaignRunner.ToWhatsAppDigits(t.Phone);

    /// <summary>Opted-in customers only (never "unknown" and never opted out), narrowed by the chosen audience.</summary>
    public async Task<List<Target>> AudienceAsync(string audience, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var all = await db.Customers.AsNoTracking().Where(c => c.MarketingStatus == MarketingStatuses.OptedIn && c.CustomerType != CustomerTypes.WalkIn)
            .Select(c => new { c.Id, c.Name, c.Phone, c.Email, c.Balance }).ToListAsync(ct);
        HashSet<int>? keep = audience switch
        {
            "Opted-in customers who owe me" => all.Where(c => c.Balance > 0).Select(c => c.Id).ToHashSet(),
            "Opted-in Gold customers" => await GoldAsync(today, ct),
            "Opted-in customers who haven't bought in 90 days" => await LapsedAsync(today, 90, ct),
            _ => null,
        };
        return all.Where(c => keep is null || keep.Contains(c.Id)).Select(c => new Target(c.Id, c.Name, c.Phone, c.Email)).ToList();
    }

    private async Task<HashSet<int>> GoldAsync(DateOnly today, CancellationToken ct)
    {
        var from = today.AddYears(-1);
        var spend = await db.Invoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.VoidedAt == null && !i.IsSample).GroupBy(i => i.CustomerId).Select(g => new { g.Key, Total = g.Sum(i => i.TotalAmount) }).ToListAsync(ct);
        return spend.Where(s => CustomerRanking.For(s.Total) == "Gold").Select(s => s.Key).ToHashSet();
    }

    public async Task<HashSet<int>> LapsedAsync(DateOnly today, int days, CancellationToken ct)
    {
        var cutoff = today.AddDays(-days);
        var last = await db.Invoices.AsNoTracking().Where(i => i.VoidedAt == null && !i.IsSample).GroupBy(i => i.CustomerId).Select(g => new { g.Key, Last = g.Max(i => i.InvoiceDate) }).ToListAsync(ct);
        return last.Where(l => l.Last <= cutoff).Select(l => l.Key).ToHashSet();
    }

    public static string Fill(string template, Target t, string? amount = null) =>
        template.Replace("{name}", string.IsNullOrWhiteSpace(t.Name) ? "there" : t.Name, StringComparison.OrdinalIgnoreCase).Replace("{amount}", amount ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes one pending message per person (skipping anyone already queued under the same key). The dispatcher sends them, inside the allowed hours.</summary>
    public async Task<int> EnqueueAsync(int recordId, string channel, string? subject, IEnumerable<(Target Who, string Text, string DedupeKey)> items, CancellationToken ct)
    {
        EnsureChannel(channel);
        var existing = (await db.CampaignRecipients.AsNoTracking().Where(r => r.RecordId == recordId && r.DedupeKey != null).Select(r => r.DedupeKey!).ToListAsync(ct)).ToHashSet();
        var rows = new List<CampaignRecipient>();
        foreach (var (who, text, key) in items)
        {
            var address = AddressFor(channel, who);
            if (address is null || !existing.Add(key)) continue;
            rows.Add(new CampaignRecipient
            {
                RecordId = recordId, CustomerId = who.CustomerId, Channel = channel, Address = address, Name = who.Name.Length > 120 ? who.Name[..120] : who.Name, Subject = subject,
                Message = text.Length > 4000 ? text[..4000] : text, DedupeKey = key, CreatedAt = clock.UtcNow,
            });
        }
        if (rows.Count == 0) return 0;
        db.CampaignRecipients.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}

/// <summary>Sends the pending broadcast messages, a few at a time, from the background schedule. Never during quiet hours; consent is read again for every message.</summary>
public sealed class BroadcastDispatcher(IBusinessDbContext db, IClock clock, IChatSender chat, ICampaignEmailSender email, ISmsSender sms, ICompanyContext company, IOptions<CampaignOptions> options)
{
    private CampaignOptions Opt => options.Value;

    public async Task<int> DispatchAsync(int max, CancellationToken ct)
    {
        if (CampaignSchedule.IsQuietHour(CampaignSchedule.LocalNow(clock.UtcNow).Minute)) return 0;
        var ids = await db.CampaignRecipients.AsNoTracking().Where(r => r.Status == RecipientStatuses.Pending).OrderBy(r => r.Id).Take(max).Select(r => r.Id).ToListAsync(ct);
        var done = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            // Claim the row with one UPDATE, so a second server (or an overlapping run) can't send the same message twice.
            if (await db.CampaignRecipients.Where(r => r.Id == id && r.Status == RecipientStatuses.Pending).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RecipientStatuses.Sending), ct) == 0) continue;
            db.ClearTracker();
            var r = await db.CampaignRecipients.SingleAsync(x => x.Id == id, ct);
            (r.Status, r.Error) = await SendOneAsync(r, ct);
            if (r.Status == RecipientStatuses.Sent) r.SentAt = clock.UtcNow;
            await db.SaveChangesAsync(ct); done++;
        }
        return done;
    }

    private async Task<(string Status, string? Error)> SendOneAsync(CampaignRecipient r, CancellationToken ct)
    {
        // STOP or "unsubscribe" may have arrived since this was queued.
        var status = r.CustomerId is int cid ? await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.MarketingStatus).FirstOrDefaultAsync(ct) : null;
        if (status != MarketingStatuses.OptedIn) return (RecipientStatuses.Skipped, "The customer has not opted in, or has opted out.");
        string? unsub = null;
        if (!string.IsNullOrEmpty(Opt.SecretKey) && !string.IsNullOrEmpty(Opt.PublicBaseUrl) && r.CustomerId is int uid) unsub = UnsubscribeLinks.Url(Opt.PublicBaseUrl, Opt.SecretKey, company.Key, uid);
        const string NoUnsub = "Needs Campaigns:SecretKey and a public base URL so every message can carry a working unsubscribe link.";

        switch (r.Channel)
        {
            case "Email":
            {
                if (!email.IsConfigured) return (RecipientStatuses.Skipped, "No email sender is configured.");
                if (unsub is null) return (RecipientStatuses.Skipped, NoUnsub);
                var subject = (r.Subject ?? company.LegalName); subject = subject.Length > 78 ? subject[..78] : subject;
                var text = $"{r.Message}\n\nTo stop receiving these emails: {unsub}";
                var html = $"<div style=\"font:15px Segoe UI,Arial,sans-serif;line-height:1.5\"><p>{WebUtility.HtmlEncode(r.Message).Replace("\n", "<br>")}</p>"
                         + $"<p style=\"color:#777;font-size:12px\">You're receiving this because you agreed to hear from {WebUtility.HtmlEncode(company.LegalName)}. <a href=\"{WebUtility.HtmlEncode(unsub)}\">Unsubscribe</a></p></div>";
                return await email.TrySendAsync(r.Address, r.Name, company.LegalName, subject, html, text, unsub, ct) ? (RecipientStatuses.Sent, null) : (RecipientStatuses.Failed, "The email provider refused the message.");
            }
            case "SMS":
            {
                if (!sms.IsConfigured) return (RecipientStatuses.Skipped, "No SMS provider is configured (Notify:SmsApiKey).");
                if (unsub is null) return (RecipientStatuses.Skipped, NoUnsub);
                return await sms.TrySendAsync(r.Address, $"{r.Message} Stop: {unsub}", ct) ? (RecipientStatuses.Sent, null) : (RecipientStatuses.Failed, "The SMS provider refused the message.");
            }
            default:
            {
                if (!chat.IsConfigured(company.Key)) return (RecipientStatuses.Skipped, "Twilio is not configured for this company.");
                if (await WindowOpenAsync(r.Address, ct))
                    return await chat.TrySendTextAsync(company.Key, r.Address, r.Message, ct) ? (RecipientStatuses.Sent, null) : (RecipientStatuses.Failed, "Twilio refused the message.");
                // WhatsApp only lets a business START a conversation with an approved template.
                if (!chat.HasTemplate(company.Key, "Broadcast"))
                    return (RecipientStatuses.Skipped, "The customer's 24-hour window is closed and no approved template is set for broadcasts (Twilio:Templates:Broadcast).");
                var vars = new Dictionary<string, string> { ["1"] = string.IsNullOrWhiteSpace(r.Name) ? "friend" : r.Name!, ["2"] = CampaignRunner.OneLine(r.Message), ["3"] = company.LegalName };
                return await chat.TrySendTemplateAsync(company.Key, r.Address, "Broadcast", vars, ct) ? (RecipientStatuses.Sent, null) : (RecipientStatuses.Failed, "Twilio refused the template message.");
            }
        }
    }

    private async Task<bool> WindowOpenAsync(string digits, CancellationToken ct)
    {
        var lastInbound = await (from m in db.ChatMessages.AsNoTracking()
                                 join cv in db.ChatConversations.AsNoTracking() on m.ConversationId equals cv.Id
                                 where cv.Channel == ChatChannels.WhatsApp && cv.ExternalId == digits && m.Role == ChatRoles.User
                                 select (DateTime?)m.CreatedAt).MaxAsync(ct);
        return lastInbound is DateTime t && clock.UtcNow - t < TimeSpan.FromHours(Opt.WindowHours);
    }
}

/// <summary>Email, SMS and WhatsApp broadcasts: pick the audience, count it, send it, read the delivery report.</summary>
public abstract class BroadcastHookBase(string app, string channel, BroadcastQueue queue) : IAppHook
{
    public string App => app;

    public Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (!isNew && r.Status == "Sent") throw new BusinessRuleException("A campaign that has been sent can't be edited.");
        r.Date ??= c.Today;
        return Task.CompletedTask;
    }

    public async Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct)
    {
        var d = Dx.Parse(r.Data);
        if (action == "report")
        {
            var rows = await c.Db.CampaignRecipients.AsNoTracking().Where(x => x.RecordId == r.Id).GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            int N(string s) => rows.FirstOrDefault(x => x.Key == s)?.N ?? 0;
            var why = await c.Db.CampaignRecipients.AsNoTracking().Where(x => x.RecordId == r.Id && x.Error != null).Select(x => x.Error!).Take(200).ToListAsync(ct);
            return new ActionResultDto($"Delivered: {N(RecipientStatuses.Sent)}\nWaiting to go out: {N(RecipientStatuses.Pending) + N(RecipientStatuses.Sending)}\nNot sent on purpose: {N(RecipientStatuses.Skipped)}\nFailed: {N(RecipientStatuses.Failed)}"
                + (why.Count > 0 ? "\n\nWhy some were not sent:\n" + string.Join("\n", why.GroupBy(w => w).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count()} × {g.Key}")) : ""));
        }
        var audience = d.Str("audience") ?? BroadcastQueue.Audiences[0];
        var targets = await queue.AudienceAsync(audience, ct);
        var reachable = targets.Where(t => BroadcastQueue.AddressFor(channel, t) is not null).ToList();
        if (action == "count")
            return new ActionResultDto($"{reachable.Count} of {targets.Count} opted-in customer(s) in \"{audience}\" can be reached by {channel}. {targets.Count - reachable.Count} have no {(channel == "Email" ? "email address" : "phone number")} on their record. Customers who haven't opted in are never messaged.");

        if (string.IsNullOrWhiteSpace(d.Str("message"))) throw new BusinessRuleException("Write the message first.");
        if (reachable.Count == 0) throw new BusinessRuleException($"No opted-in customer in \"{audience}\" can be reached by {channel}.");
        var n = await queue.EnqueueAsync(r.Id, channel, d.Str("subject"), reachable.Select(t => (t, BroadcastQueue.Fill(d.Str("message")!, t), $"c{t.CustomerId}")), ct);
        d.Set("recipients", n); d.Set("sentOn", c.Today);
        r.Data = d.ToJsonString(); r.Status = "Sent"; r.UpdatedAt = c.Clock.UtcNow; r.Date = c.Today;
        return new ActionResultDto($"Queued for {n} customer(s). They go out between 07:00 and 20:30, a few at a time; open the delivery report to follow it.");
    }

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var ids = await c.Db.AppRecords.AsNoTracking().Where(x => x.App == app && x.Kind == "campaign").Select(x => x.Id).ToListAsync(ct);
        var rows = await c.Db.CampaignRecipients.AsNoTracking().Where(x => ids.Contains(x.RecordId)).GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int N(string s) => rows.FirstOrDefault(x => x.Key == s)?.N ?? 0;
        return [new Card("Delivered, all time", N(RecipientStatuses.Sent).ToString(), null, "ok"), new Card("Failed", N(RecipientStatuses.Failed).ToString(), null, N(RecipientStatuses.Failed) > 0 ? "bad" : "")];
    }
}

public sealed class EmailMarketingHook(BroadcastQueue q) : BroadcastHookBase("email-marketing", "Email", q);
public sealed class SmsMarketingHook(BroadcastQueue q) : BroadcastHookBase("sms-marketing", "SMS", q);
public sealed class WhatsAppMarketingHook(BroadcastQueue q) : BroadcastHookBase("whatsapp", "WhatsApp", q);

/// <summary>The rules behind Marketing Automation: who matches each, and queueing a message to them once.</summary>
public sealed class AutomationService(IBusinessDbContext db, IClock clock, BroadcastQueue queue)
{
    public const string First = "A customer buys for the first time";
    public const string Lapsed = "A customer hasn't bought for a while";
    public const string Overdue = "An invoice is overdue";

    /// <summary>Everyone the rule matches today and hasn't already been queued: who, the personalised text and the key that stops a repeat. Only customers who opted in.</summary>
    public async Task<List<(Target Who, string Text, string Key)>> MatchesAsync(AppRecord rule, CancellationToken ct)
    {
        var d = Dx.Parse(rule.Data); var channel = d.Str("channel") ?? "Email";
        var template = d.Str("message") ?? ""; var days = Math.Max(0, d.Int("days") ?? 30); var today = clock.BusinessToday;
        var customers = await db.Customers.AsNoTracking().Where(c => c.MarketingStatus == MarketingStatuses.OptedIn && c.CustomerType != CustomerTypes.WalkIn).Select(c => new { c.Id, c.Name, c.Phone, c.Email }).ToListAsync(ct);
        var who = customers.ToDictionary(c => c.Id, c => new Target(c.Id, c.Name, c.Phone, c.Email));
        var found = new List<(Target, string, string)>();
        switch (d.Str("trigger"))
        {
            case First:
                var firsts = await db.Invoices.AsNoTracking().Where(i => i.VoidedAt == null && !i.IsSample).GroupBy(i => i.CustomerId).Select(g => new { g.Key, First = g.Min(i => i.InvoiceDate) }).ToListAsync(ct);
                foreach (var f in firsts.Where(f => f.First >= today.AddDays(-7) && who.ContainsKey(f.Key))) found.Add((who[f.Key], BroadcastQueue.Fill(template, who[f.Key]), $"first-{f.Key}"));
                break;
            case Lapsed:
                var last = await db.Invoices.AsNoTracking().Where(i => i.VoidedAt == null && !i.IsSample).GroupBy(i => i.CustomerId).Select(g => new { g.Key, Last = g.Max(i => i.InvoiceDate) }).ToListAsync(ct);
                foreach (var l in last.Where(l => l.Last <= today.AddDays(-Math.Max(1, days)) && who.ContainsKey(l.Key))) found.Add((who[l.Key], BroadcastQueue.Fill(template, who[l.Key]), $"lapsed-{l.Key}-{l.Last:yyyyMMdd}"));
                break;
            case Overdue:
                var due = await db.Invoices.AsNoTracking().Where(i => i.VoidedAt == null && !i.IsSample && i.Status != PaymentStatuses.Paid && i.DueDate != null && i.DueDate < today.AddDays(-days)).Select(i => new { i.Id, i.CustomerId, i.TotalAmount, i.AmountPaid }).ToListAsync(ct);
                foreach (var i in due.Where(i => who.ContainsKey(i.CustomerId) && i.TotalAmount > i.AmountPaid)) found.Add((who[i.CustomerId], BroadcastQueue.Fill(template, who[i.CustomerId], AppHelpers.Naira(i.TotalAmount - i.AmountPaid)), $"overdue-{i.Id}"));
                break;
            default: throw new BusinessRuleException("This rule has no trigger.");
        }
        var done = (await db.CampaignRecipients.AsNoTracking().Where(r => r.RecordId == rule.Id && r.DedupeKey != null).Select(r => r.DedupeKey!).ToListAsync(ct)).ToHashSet();
        return found.Where(f => !done.Contains(f.Item3) && BroadcastQueue.AddressFor(channel, f.Item1) is not null).ToList();
    }

    public async Task<int> RunRuleAsync(AppRecord rule, CancellationToken ct)
    {
        var d = Dx.Parse(rule.Data);
        var matches = await MatchesAsync(rule, ct);
        return matches.Count == 0 ? 0 : await queue.EnqueueAsync(rule.Id, d.Str("channel") ?? "Email", d.Str("subject"), matches, ct);
    }

    /// <summary>Runs every rule that is On. Used by the hourly schedule.</summary>
    public async Task<int> RunAllAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var id in await db.AppRecords.AsNoTracking().Where(r => r.App == "marketing-automation" && r.Kind == "rule" && r.Status == "On").Select(r => r.Id).ToListAsync(ct))
        {
            var rule = await db.AppRecords.SingleAsync(r => r.Id == id, ct);
            try { total += await RunRuleAsync(rule, ct); }
            catch (BusinessRuleException) { /* the channel isn't set up: leave the rule alone until it is */ }
        }
        return total;
    }
}

public sealed class AutomationHook(AutomationService svc) : IAppHook
{
    public string App => "marketing-automation";

    public Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (data.Str("channel") == "Email" && string.IsNullOrWhiteSpace(data.Str("subject"))) throw new BusinessRuleException("Give the email a subject.");
        if ((data.Int("days") ?? 0) < 0) throw new BusinessRuleException("Days can't be negative.");
        return Task.CompletedTask;
    }

    public async Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct)
    {
        if (action == "test")
        {
            var m = await svc.MatchesAsync(r, ct);
            return new ActionResultDto(m.Count == 0 ? "Nobody matches right now (or everyone who does has already been queued, or hasn't opted in)." : $"{m.Count} opted-in customer(s) would get it now, for example: {string.Join(", ", m.Take(5).Select(x => x.Who.Name))}.");
        }
        var n = await svc.RunRuleAsync(r, ct);
        return new ActionResultDto(n == 0 ? "Nobody to send to right now." : $"Queued for {n} customer(s). They go out between 07:00 and 20:30.");
    }
}
