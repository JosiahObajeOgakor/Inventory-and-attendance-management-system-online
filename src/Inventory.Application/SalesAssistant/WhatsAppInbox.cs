using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Application.SalesAssistant;

public sealed class InboxOptions
{
    public const string Section = "Inbox";
    /// <summary>How many times a message is tried before it is marked Failed and a person is alerted.</summary>
    public int MaxAttempts { get; set; } = 4;
    /// <summary>How many customers are answered at the same time, per company.</summary>
    public int Concurrency { get; set; } = 4;
    /// <summary>How long a worker may hold a message before another may take it over (covers a crashed worker).</summary>
    public int LeaseSeconds { get; set; } = 180;
    /// <summary>Answered and failed messages are deleted after this many days (chat history itself is kept separately).</summary>
    public int KeepDays { get; set; } = 30;
}

/// <summary>
/// The WhatsApp inbox: a durable queue in the company's own database. The webhook stores a message and acknowledges Meta at once;
/// <see cref="WhatsAppInboxProcessor"/> answers it in the background. Guarantees:
/// <list type="bullet">
/// <item>A message is stored once, whatever Meta redelivers (unique channel + message id).</item>
/// <item>One customer's messages are answered in the order they were sent, one at a time; different customers run in parallel.</item>
/// <item>Several workers (or servers) can claim at once without taking the same message (<c>FOR UPDATE SKIP LOCKED</c> + a lease).</item>
/// <item>A worker that dies mid-answer only delays the message until its lease runs out.</item>
/// </list>
/// </summary>
public sealed class WhatsAppInbox(IBusinessDbContext db, TransactionRunner tx, IClock clock, IOptions<InboxOptions> options)
{
    private InboxOptions Opt => options.Value;

    /// <summary>Stores a received message. False when it was already stored (a redelivery), which the caller simply acknowledges.</summary>
    public async Task<bool> EnqueueAsync(string channel, string sender, string externalId, string? text, string? buttonId, CancellationToken ct)
    {
        if (await db.InboundMessages.AsNoTracking().AnyAsync(m => m.Channel == channel && m.ExternalId == externalId, ct)) return false;
        var now = clock.UtcNow;
        db.InboundMessages.Add(new InboundMessage
        {
            Channel = channel, Sender = sender, ExternalId = externalId, Text = text is { Length: > 4096 } ? text[..4096] : text, ButtonId = buttonId,
            Status = InboundStatuses.Pending, NextAttemptAt = now, ReceivedAt = now,
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { db.ClearTracker(); return false; }   // lost a race with a simultaneous redelivery
    }

    /// <summary>
    /// Takes up to <paramref name="max"/> messages that are due, at most one per customer and only each customer's oldest unanswered one,
    /// and leases them to the caller. Returns their ids.
    /// </summary>
    public Task<List<long>> ClaimAsync(int max, CancellationToken ct) =>
        tx.RunAsync(async inner =>
        {
            var now = clock.UtcNow;
            var due = await db.InboundMessages.FromSqlInterpolated($"""
                SELECT m.* FROM inbound_messages m
                WHERE m.Status = 'Pending' AND m.NextAttemptAt <= {now} AND (m.LockedUntil IS NULL OR m.LockedUntil < {now})
                  AND NOT EXISTS (SELECT 1 FROM inbound_messages e WHERE e.Sender = m.Sender AND e.Channel = m.Channel AND e.Status = 'Pending' AND e.Id < m.Id)
                ORDER BY m.Id LIMIT {max} FOR UPDATE SKIP LOCKED
                """).ToListAsync(inner);
            foreach (var m in due) { m.LockedUntil = now.AddSeconds(Opt.LeaseSeconds); m.Attempts++; }
            await db.SaveChangesAsync(inner);
            return due.Select(m => m.Id).ToList();
        }, ct);

    /// <summary>Wait before the next try: 15 s, 1 min, 5 min, 15 min…</summary>
    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(15),
        2 => TimeSpan.FromMinutes(1),
        3 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15),
    };

    /// <summary>Deletes answered/failed messages older than <see cref="InboxOptions.KeepDays"/>. Returns how many.</summary>
    public Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-Opt.KeepDays);
        return db.InboundMessages.Where(m => m.Status != InboundStatuses.Pending && m.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>Messages waiting longer than <paramref name="olderThan"/> — the monitor alerts on this.</summary>
    public Task<int> StuckCountAsync(TimeSpan olderThan, CancellationToken ct)
    {
        var cutoff = clock.UtcNow - olderThan;
        return db.InboundMessages.CountAsync(m => m.Status == InboundStatuses.Pending && m.ReceivedAt < cutoff, ct);
    }
}

/// <summary>The reply as stored between "generated" and "sent", so a retry never asks the model twice.</summary>
public sealed record InboxReply(string Text, IReadOnlyList<string>? PaymentOptions, decimal? OrderTotal, bool TextSent = false);

/// <summary>Answers one claimed inbox message: generate (or reuse) the reply, send it, record the outcome; retry or alert on failure.</summary>
public sealed class WhatsAppInboxProcessor(
    IBusinessDbContext db, SalesAssistantService assistant, IWhatsAppSender wa, ICompanyContext company, IAdminNotifier admin,
    IClock clock, IOptions<InboxOptions> options, IOptions<FulfilmentOptions> fulfilment, ILogger<WhatsAppInboxProcessor> log)
{
    public const string PayButtonPrefix = "pay:";
    private const string Sorry = "Sorry, I'm having trouble answering right now. A member of our team has been told and will reply to you here shortly.";

    public async Task ProcessAsync(long id, CancellationToken ct)
    {
        var m = await db.InboundMessages.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (m is null || m.Status != InboundStatuses.Pending) return;
        try
        {
            var reply = m.ReplyJson is { } saved ? JsonSerializer.Deserialize<InboxReply>(saved)! : await GenerateAsync(m, ct);
            if (m.ReplyJson is null) { m.ReplyJson = JsonSerializer.Serialize(reply); await db.SaveChangesAsync(ct); }

            if (!reply.TextSent && reply.Text.Length > 0)
            {
                await wa.SendTextAsync(company.Key, m.Sender, reply.Text, ct);
                reply = reply with { TextSent = true };
                m.ReplyJson = JsonSerializer.Serialize(reply); await db.SaveChangesAsync(ct);
            }
            if (reply.PaymentOptions is { Count: > 0 } pay)
            {
                var total = reply.OrderTotal is decimal t ? $"₦{t:N2}" : "your order";
                await wa.SendButtonsAsync(company.Key, m.Sender, $"How would you like to pay {total}?",
                    pay.Select(p => new WhatsAppButton(PayButtonPrefix + p, "Pay with " + PaymentProviders.DisplayName(p))).ToList(), ct);
            }

            m.Status = InboundStatuses.Done; m.ProcessedAt = clock.UtcNow; m.LockedUntil = null; m.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Inbox message {Id} failed (attempt {Attempt})", m.Id, m.Attempts);
            m.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            m.LockedUntil = null;
            if (m.Attempts >= options.Value.MaxAttempts)
            {
                m.Status = InboundStatuses.Failed; m.ProcessedAt = clock.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await GiveUpAsync(m);
            }
            else
            {
                m.NextAttemptAt = clock.UtcNow + WhatsAppInbox.Backoff(m.Attempts);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    private async Task<InboxReply> GenerateAsync(InboundMessage m, CancellationToken ct)
    {
        // Any failure here (OpenAI down, a timeout, "assistant not switched on") is retried and then escalated to a person —
        // never shown to the customer as if it were an answer.
        SalesAssistantReply r;
        // A tapped payment button goes straight to the processor, no model involved.
        if (m.ButtonId is { } b && b.StartsWith(PayButtonPrefix, StringComparison.Ordinal))
        {
            var provider = b[PayButtonPrefix.Length..];
            if (!PaymentProviders.IsValid(provider)) return new InboxReply("", null, null);
            r = await assistant.ChoosePaymentAsync(m.Channel, m.Sender, provider, ct);
        }
        else if (!string.IsNullOrWhiteSpace(m.Text)) r = await assistant.AskAsync(m.Channel, m.Sender, m.Text, ct);
        else return new InboxReply("", null, null);
        return new InboxReply(r.Text, r.PaymentOptions, r.OrderTotal);
    }

    /// <summary>Tried too many times: tell the customer a person will reply, and tell the admin (email/SMS, and WhatsApp when possible).</summary>
    private async Task GiveUpAsync(InboundMessage m)
    {
        var what = m.Text is { Length: > 0 } t ? $"\"{(t.Length > 200 ? t[..200] + "…" : t)}\"" : $"a button tap ({m.ButtonId})";
        var alert = $"A WhatsApp message from +{m.Sender.TrimStart('+')} could not be answered automatically after {m.Attempts} tries: {what}. " +
                    $"Last error: {m.LastError}. Please reply to the customer yourself.";
        try { await wa.SendTextAsync(company.Key, m.Sender, Sorry, CancellationToken.None); } catch (Exception ex) { log.LogWarning(ex, "Could not send the apology"); }
        try { await admin.NotifyAsync("WhatsApp message needs a person", alert, CancellationToken.None); } catch (Exception ex) { log.LogError(ex, "Could not alert the admin"); }
        // Best effort: WhatsApp only delivers free text if the admin messaged the business number in the last 24 hours.
        var to = fulfilment.Value.AdminWhatsApp;
        if (!string.IsNullOrWhiteSpace(to))
            try { await wa.SendTextAsync(company.Key, OrderDispatchService.International(to), alert, CancellationToken.None); } catch { /* email above is the reliable channel */ }
    }
}
