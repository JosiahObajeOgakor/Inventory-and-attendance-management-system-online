using System.Net;
using System.Text.RegularExpressions;
using Inventory.Application.Abstractions;
using Inventory.Application.Messaging;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Application.Campaigns;

public sealed record RunResult(int Planned, int Sent, int Failed, int Skipped, IReadOnlyList<string> Samples, string Note);

/// <summary>
/// Carries out one campaign run: plan (who/what, from the database), write (fresh wording, facts filled in), send (WhatsApp / email) and record every
/// attempt. The rules that protect customers live here and cannot be switched off from the screen: opted-in only, one message per person per cooldown,
/// nothing before 07:00 or after 20:30, free text only inside WhatsApp's 24-hour window (an approved template otherwise), STOP honoured.
/// </summary>
public sealed class CampaignRunner(IBusinessDbContext db, CampaignPlanner planner, CampaignWriter writer, IChatSender chat, ICampaignEmailSender email, IClock clock,
    ICompanyContext company, IOptions<CampaignOptions> options, ILogger<CampaignRunner> log)
{
    private CampaignOptions Opt => options.Value;

    public async Task<RunResult> RunAsync(Campaign c, bool preview, CancellationToken ct)
    {
        if (c.Type == CampaignTypes.AdminRestock) return await RunAdminAsync(c, preview, ct);

        if (!preview && CampaignSchedule.IsQuietHour(CampaignSchedule.LocalNow(clock.UtcNow).Minute))
            return new RunResult(0, 0, 0, 0, [], "Quiet hours (customers are not messaged before 07:00 or after 20:30 Lagos time).");

        var targets = await planner.PlanAsync(c, ct);
        int sent = 0, failed = 0, skipped = 0;
        var samples = new List<string>();
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            var variants = await writer.VariantsAsync(t.Kind, t.Language, ct);
            var body = CampaignWriter.Render(variants, t.Facts, (t.CustomerId ?? 0) + clock.BusinessToday.DayNumber);
            var wa = body + "\n\n" + CampaignText.Closing(t.Kind, t.Language);
            if (preview) { if (samples.Count < 5) samples.Add($"{t.Name} ({t.Kind}, {t.Language}): {wa}"); continue; }

            var delivered = false;
            if (c.SendWhatsApp && !string.IsNullOrWhiteSpace(t.Phone))
            {
                var r = await SendWhatsAppAsync(c, t, wa, ct);
                (sent, failed, skipped) = Tally(r, sent, failed, skipped);
                delivered |= r == CampaignMessageStatuses.Sent;
            }
            if (c.SendEmail && !string.IsNullOrWhiteSpace(t.Email))
            {
                var r = await SendEmailAsync(c, t, body, ct);
                (sent, failed, skipped) = Tally(r, sent, failed, skipped);
                delivered |= r == CampaignMessageStatuses.Sent;
            }
            if (delivered && t.Prime != PrimeKind.None && !string.IsNullOrWhiteSpace(t.Phone)) await PrimeAsync(t, ct);
        }
        var note = preview ? $"{targets.Count} customer(s) would be messaged." : $"{sent} sent, {failed} failed, {skipped} skipped, {writer.AiCalls} AI call(s).";
        return new RunResult(targets.Count, sent, failed, skipped, samples, note);
    }

    private static (int, int, int) Tally(string status, int sent, int failed, int skipped) =>
        status == CampaignMessageStatuses.Sent ? (sent + 1, failed, skipped) : status == CampaignMessageStatuses.Failed ? (sent, failed + 1, skipped) : (sent, failed, skipped + 1);

    // ------------------------------------------------------------------ WhatsApp

    public static string ToWhatsAppDigits(string phone)
    {
        var d = new string(phone.Where(char.IsDigit).ToArray());
        if (d.StartsWith("234")) return d;
        if (d.StartsWith('0') && d.Length == 11) return "234" + d[1..];
        return d.Length == 10 ? "234" + d : d;
    }

    private async Task<bool> WindowOpenAsync(string digits, CancellationToken ct)
    {
        var lastInbound = await (from m in db.ChatMessages.AsNoTracking()
                                 join cv in db.ChatConversations.AsNoTracking() on m.ConversationId equals cv.Id
                                 where cv.Channel == ChatChannels.WhatsApp && cv.ExternalId == digits && m.Role == ChatRoles.User
                                 select (DateTime?)m.CreatedAt).MaxAsync(ct);
        return lastInbound is DateTime t && clock.UtcNow - t < TimeSpan.FromHours(Opt.WindowHours);
    }

    private async Task<string> SendWhatsAppAsync(Campaign c, Target t, string text, CancellationToken ct)
    {
        var digits = ToWhatsAppDigits(t.Phone!);
        if (!chat.IsConfigured(company.Key)) return await LogAsync(c, t, digits, "WhatsApp", text, CampaignMessageStatuses.Skipped, "Twilio is not configured for this company.", null, ct);
        if (await WindowOpenAsync(digits, ct))
        {
            var ok = await chat.TrySendTextAsync(company.Key, digits, text, ct);
            return await LogAsync(c, t, digits, "WhatsApp", text, ok ? CampaignMessageStatuses.Sent : CampaignMessageStatuses.Failed, ok ? null : "Twilio refused the message.", "text", ct);
        }
        // WhatsApp only lets a business START a conversation with an approved template. The variables carry the freshly written text.
        if (!chat.HasTemplate(company.Key, c.Type))
            return await LogAsync(c, t, digits, "WhatsApp", text, CampaignMessageStatuses.Skipped, $"The customer's 24-hour window is closed and no approved template is set for {c.Type} (Twilio:Templates:{c.Type}).", null, ct);
        var vars = new Dictionary<string, string> { ["1"] = t.Facts.GetValueOrDefault("name", "friend"), ["2"] = OneLine(text), ["3"] = company.LegalName };
        var sent = await chat.TrySendTemplateAsync(company.Key, digits, c.Type, vars, ct);
        return await LogAsync(c, t, digits, "WhatsApp", text, sent ? CampaignMessageStatuses.Sent : CampaignMessageStatuses.Failed, sent ? null : "Twilio refused the template message.", "template", ct);
    }

    /// <summary>Template variables may not contain line breaks, tabs or runs of spaces.</summary>
    public static string OneLine(string s) => Regex.Replace(s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '), @"\s{2,}", " ").Trim();

    /// <summary>A reply of YES to the message must mean something: put the chat into the state that matches what the message offered.</summary>
    private async Task PrimeAsync(Target t, CancellationToken ct)
    {
        var digits = ToWhatsAppDigits(t.Phone!);
        var conv = await db.ChatConversations.FirstOrDefaultAsync(cv => cv.Channel == ChatChannels.WhatsApp && cv.ExternalId == digits, ct);
        if (conv is null)
        {
            conv = new ChatConversation { Channel = ChatChannels.WhatsApp, ExternalId = digits, CreatedAt = clock.UtcNow, LastMessageAt = clock.UtcNow };
            db.ChatConversations.Add(conv);
        }
        else if (conv.State is ChatStates.AwaitingPayment or ChatStates.AwaitingConfirm or ChatStates.AwaitingName or ChatStates.AwaitingConsent) return;   // never overwrite an order in progress
        var name = t.Name.All(char.IsDigit) ? null : t.Name;
        var (state, json) = t.Prime == PrimeKind.Order ? ChatFlowService.PrimedOrder(t.Lines ?? [], name) : ChatFlowService.PrimedAskQuantity(t.ProductId ?? 0, name);
        conv.State = state; conv.DraftJson = json; conv.QuotationId = null; conv.AiCalls = 0; conv.CustomerId = t.CustomerId; conv.Language = t.Language;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ email

    private async Task<string> SendEmailAsync(Campaign c, Target t, string body, CancellationToken ct)
    {
        var subject = $"{t.Facts.GetValueOrDefault("product") ?? company.LegalName} · {company.LegalName}";
        if (subject.Length > 78) subject = subject[..78];
        if (!email.IsConfigured) return await LogAsync(c, t, t.Email!, "Email", body, CampaignMessageStatuses.Skipped, "No email sender is configured (SendGrid__ApiKey or Smtp).", null, ct);
        if (string.IsNullOrEmpty(Opt.SecretKey) || string.IsNullOrEmpty(Opt.PublicBaseUrl) || t.CustomerId is null)
            return await LogAsync(c, t, t.Email!, "Email", body, CampaignMessageStatuses.Skipped, "Email needs Campaigns:SecretKey and a public base URL so every email can carry a working unsubscribe link.", null, ct);
        var unsub = UnsubscribeLinks.Url(Opt.PublicBaseUrl, Opt.SecretKey, company.Key, t.CustomerId.Value);
        var text = $"{body}\n\nTo stop receiving these emails: {unsub}";
        var html = $"<div style=\"font:15px Segoe UI,Arial,sans-serif;line-height:1.5\"><p>{WebUtility.HtmlEncode(body)}</p>"
                 + $"<p style=\"color:#777;font-size:12px\">You're receiving this because you agreed to hear from {WebUtility.HtmlEncode(company.LegalName)}. <a href=\"{WebUtility.HtmlEncode(unsub)}\">Unsubscribe</a></p></div>";
        var ok = await email.TrySendAsync(t.Email!, t.Name, company.LegalName, subject, html, text, unsub, ct);
        return await LogAsync(c, t, t.Email!, "Email", body, ok ? CampaignMessageStatuses.Sent : CampaignMessageStatuses.Failed, ok ? null : "The email provider refused the message.", "email", ct);
    }

    // ------------------------------------------------------------------ the admin's restock digest

    private async Task<RunResult> RunAdminAsync(Campaign c, bool preview, CancellationToken ct)
    {
        var digest = await planner.AdminDigestAsync(c, Opt.AdminName, ct);
        if (digest is null) return new RunResult(0, 0, 0, 0, [], "Nothing is about to run out, so no digest was sent.");
        if (preview) return new RunResult(1, 0, 0, 0, [digest.Text], "Sample of today's digest.");

        int sent = 0, failed = 0, skipped = 0;
        var target = new Target(null, string.IsNullOrWhiteSpace(Opt.AdminName) ? "Admin" : Opt.AdminName, Opt.AdminPhone, Opt.AdminEmail, ChatLanguages.English, "admin", new() { ["name"] = Opt.AdminName });
        if (c.SendWhatsApp && !string.IsNullOrWhiteSpace(Opt.AdminPhone))
        {
            var r = await SendWhatsAppAsync(c, target, digest.Text, ct);
            (sent, failed, skipped) = Tally(r, sent, failed, skipped);
        }
        if (c.SendEmail && !string.IsNullOrWhiteSpace(Opt.AdminEmail))
        {
            if (!email.IsConfigured) { await LogAsync(c, target, Opt.AdminEmail, "Email", digest.Text, CampaignMessageStatuses.Skipped, "No email sender is configured.", null, ct); skipped++; }
            else
            {
                var html = $"<div style=\"font:15px Segoe UI,Arial,sans-serif;line-height:1.5\">{string.Join("", digest.Text.Split('\n').Select(l => $"<p>{WebUtility.HtmlEncode(l)}</p>"))}</div>";
                var ok = await email.TrySendAsync(Opt.AdminEmail, Opt.AdminName, company.LegalName, digest.Subject, html, digest.Text, "", ct);
                await LogAsync(c, target, Opt.AdminEmail, "Email", digest.Text, ok ? CampaignMessageStatuses.Sent : CampaignMessageStatuses.Failed, ok ? null : "The email provider refused the message.", "email", ct);
                if (ok) sent++; else failed++;
            }
        }
        return new RunResult(1, sent, failed, skipped, [], $"Digest of {digest.Products} product(s): {sent} sent, {failed} failed, {skipped} skipped.");
    }

    private async Task<string> LogAsync(Campaign c, Target t, string recipient, string channel, string text, string status, string? detail, string? mode, CancellationToken ct)
    {
        db.CampaignMessages.Add(new CampaignMessage
        {
            CampaignId = c.Id, CustomerId = t.CustomerId, Recipient = recipient.Length > 120 ? recipient[..120] : recipient, Channel = channel, Language = t.Language,
            Text = text.Length > 2000 ? text[..2000] : text, Status = status, Detail = detail, Mode = mode, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        if (status == CampaignMessageStatuses.Failed) log.LogWarning("Campaign {Type} message to {Channel} failed: {Detail}", c.Type, channel, detail);
        return status;
    }
}
