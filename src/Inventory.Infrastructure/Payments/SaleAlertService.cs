using Inventory.Application.Documents;
using Inventory.Application.Email;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Domain;
using Inventory.Application.Abstractions;
using Inventory.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Payments;

/// <summary>
/// The moment a sale is complete: the admin's WhatsApp number gets the full detail, and the admin, every clerk who works in this business and the customer
/// get an email with the receipt. Clerks are never sent WhatsApp. Each recipient is tried on its own, so one bad address or a WhatsApp 24-hour-window
/// refusal can never stop the others (and never undoes the sale).
/// </summary>
public sealed class SaleAlertService(IChatSender whatsApp, IEmailSender email, IOptions<NotifyOptions> options, UserManager<AppUser> users, ICompanyContext company,
    IServiceProvider services, IDocumentRenderer renderer, ILogger<SaleAlertService> log) : ISaleAlerts
{
    /// <summary>Twilio template (Twilio:Templates:AdminSale, an approved HX… SID) used when the admin's chat is outside WhatsApp's 24-hour window. Variables: 1 invoice, 2 buyer, 3 total, 4 items, 5 channel.</summary>
    public const string AdminTemplate = "AdminSale";

    public async Task SaleCompletedAsync(SaleAlert a, CancellationToken ct)
    {
        var o = options.Value;
        await TellAdminOnWhatsAppAsync(a, o, ct);
        await EmailEveryoneAsync(a, o, ct);
    }

    private async Task TellAdminOnWhatsAppAsync(SaleAlert a, NotifyOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.AdminPhone)) { log.LogInformation("No Notify:AdminPhone is set, so the admin was not told on WhatsApp."); return; }
        if (!whatsApp.IsConfigured(company.Key)) { log.LogWarning("Twilio isn't configured for {Company}: the admin was not told on WhatsApp.", company.Key); return; }
        var to = AdminNotifier.International(o.AdminPhone);
        try
        {
            if (await whatsApp.TrySendTextAsync(company.Key, to, SaleAlertText.Admin(a), ct)) return;
            // Free text only reaches a chat that wrote to the business number in the last 24 hours; otherwise an approved template is the only way in.
            if (whatsApp.HasTemplate(company.Key, AdminTemplate)
                && await whatsApp.TrySendTemplateAsync(company.Key, to, AdminTemplate, new Dictionary<string, string>
                {
                    ["1"] = a.InvoiceNumber, ["2"] = a.CustomerName + (string.IsNullOrWhiteSpace(a.CustomerPhone) ? "" : " " + a.CustomerPhone),
                    ["3"] = $"₦{a.Total:N0}", ["4"] = string.Join(", ", a.Lines.Select(l => $"{l.Quantity} x {l.Product}")), ["5"] = a.Channel,
                }, ct)) return;
            log.LogWarning("The admin's WhatsApp was not reached for sale {Invoice} (outside the 24-hour window and no {Template} template).", a.InvoiceNumber, AdminTemplate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Admin WhatsApp alert failed for sale {Invoice}", a.InvoiceNumber); }
    }

    private async Task EmailEveryoneAsync(SaleAlert a, NotifyOptions o, CancellationToken ct)
    {
        if (!email.IsConfigured) { log.LogInformation("Email isn't set up, so no sale emails were sent."); return; }
        // DocumentQueries is resolved here, not injected: it uses the payment service that raises this alert, and injecting it would make a circle.
        IReadOnlyList<EmailAttachment> receipt = [];
        try { var doc = await services.GetRequiredService<DocumentQueries>().ReceiptAsync(a.InvoiceId, ct); receipt = [new EmailAttachment($"Receipt-{a.InvoiceNumber}.pdf", renderer.Receipt(doc), "application/pdf")]; }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not build the receipt PDF for {Invoice}; emailing without it", a.InvoiceNumber); }

        var staff = new List<string>();
        if (!string.IsNullOrWhiteSpace(o.AdminEmail)) staff.Add(o.AdminEmail.Trim());
        foreach (var role in new[] { RoleNames.Clerk, RoleNames.LegacyClerk })
            foreach (var u in await users.GetUsersInRoleAsync(role))
                if (u.IsActive && u.CanOpen(company.Key) && !string.IsNullOrWhiteSpace(u.Email)) staff.Add(u.Email!.Trim());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var to in staff.Where(seen.Add))
            await TrySendAsync(new EmailMessage(to, null, SaleAlertText.StaffSubject(a), SaleAlertText.Html(a, false), SaleAlertText.Text(a, false), receipt, a.Company, null), a, ct);
        if (!string.IsNullOrWhiteSpace(a.CustomerEmail) && seen.Add(a.CustomerEmail.Trim()))
            await TrySendAsync(new EmailMessage(a.CustomerEmail.Trim(), a.CustomerName, SaleAlertText.CustomerSubject(a), SaleAlertText.Html(a, true), SaleAlertText.Text(a, true), receipt, a.Company, null), a, ct);
    }

    private async Task TrySendAsync(EmailMessage m, SaleAlert a, CancellationToken ct)
    {
        try { await email.SendAsync(m, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Sale email to {To} failed for {Invoice}", m.To, a.InvoiceNumber); }
    }
}
