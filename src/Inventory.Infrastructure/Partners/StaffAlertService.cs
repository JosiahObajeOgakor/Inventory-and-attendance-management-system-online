using System.Net;
using Inventory.Application.Abstractions;
using Inventory.Application.Email;
using Inventory.Application.Messaging;
using Inventory.Application.Partners;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Payments;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Partners;

/// <summary>
/// Trade-partner and wallet news for staff: the admin on WhatsApp, SMS and email; every clerk of this business gets the email as a copy (never WhatsApp or SMS).
/// Partners hear on WhatsApp (when their chat is open), SMS and email. Every channel and recipient is tried on its own and a failure is only logged.
/// </summary>
public sealed class StaffAlertService(IChatSender whatsApp, ISmsSender sms, IEmailSender email, IOptions<NotifyOptions> options, UserManager<AppUser> users,
    ICompanyContext company, IBrandedEmail branded, ILogger<StaffAlertService> log) : IStaffAlerts
{
    public async Task TellStaffAsync(StaffAlert a, CancellationToken ct)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.AdminPhone))
        {
            var to = AdminNotifier.International(o.AdminPhone);
            await Try("admin WhatsApp", () => whatsApp.IsConfigured(company.Key) ? whatsApp.TrySendTextAsync(company.Key, to, a.Text, ct) : Task.FromResult(false));
            await Try("admin SMS", () => sms.IsConfigured ? sms.TrySendAsync(to, Short(a.Text), ct) : Task.FromResult(false));
        }
        if (!email.IsConfigured) return;
        var staff = new List<string>();
        if (!string.IsNullOrWhiteSpace(o.AdminEmail)) staff.Add(o.AdminEmail.Trim());
        foreach (var role in a.AdminOnly ? Array.Empty<string>() : new[] { RoleNames.Clerk, RoleNames.LegacyClerk })
            foreach (var u in await users.GetUsersInRoleAsync(role))
                if (u.IsActive && u.CanOpen(company.Key) && !string.IsNullOrWhiteSpace(u.Email)) staff.Add(u.Email!.Trim());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var to in staff.Where(seen.Add))
            await Try("staff email", async () => { await email.SendAsync(new EmailMessage(to, null, a.Subject, a.Html ?? Html(a.Text), a.Text, [], company.LegalName, null), ct); return true; });
    }

    public async Task TellContactAsync(ContactAlert a, CancellationToken ct)
    {
        var to = new string(a.Phone.Where(char.IsDigit).ToArray());
        // WhatsApp free text only reaches a chat that wrote in the last 24 hours, so SMS goes too: one of them lands.
        var onWhatsApp = to.Length > 0 && whatsApp.IsConfigured(company.Key) && await Try("partner WhatsApp", () => whatsApp.TrySendTextAsync(company.Key, to, a.Text, ct));
        if (!onWhatsApp && to.Length > 0 && sms.IsConfigured) await Try("partner SMS", () => sms.TrySendAsync(to, Short(a.Text), ct));
        if (branded.IsConfigured && !string.IsNullOrWhiteSpace(a.Email))
            await Try("partner email", async () =>
            {
                var heading = a.Subject.Contains(": ") ? a.Subject[(a.Subject.IndexOf(": ", StringComparison.Ordinal) + 2)..] : a.Subject;
                await branded.SendAsync(new BrandedMail(a.Email.Trim(), a.Name, a.Subject, char.ToUpperInvariant(heading[0]) + heading[1..], a.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries),
                    [company.Key], ButtonText: a.ButtonText, ButtonUrl: a.ButtonUrl, Footnote: "Any questions? Reply to this email or message us on WhatsApp."), ct);
                return true;
            });
    }

    private async Task<bool> Try(string what, Func<Task<bool>> send)
    {
        try { return await send(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "{What} failed", what); return false; }
    }

    private static string Short(string text) => text.Length <= 459 ? text : text[..456] + "...";   // three SMS pages at most
    private static string Html(string text) =>
        $"<div style=\"font:15px Segoe UI,Arial,sans-serif;color:#1c2130;white-space:pre-line\">{WebUtility.HtmlEncode(text)}</div>";
}
