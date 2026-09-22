using System.Collections.Concurrent;
using System.Net.Mail;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Documents;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Application.Email;

public sealed record EmailSent(string To, string Subject, string Attachment);
public sealed record EmailPreview(string Subject, string Html, string To);

/// <summary>Emails a quotation or price list to a customer with the PDF attached, from the business's own mailbox. Every send is audited.</summary>
public sealed class DocumentEmailService(DocumentQueries docs, IDocumentRenderer renderer, IEmailSender sender, IBusinessDbContext db, ICompanyContext company, IClock clock, IOptions<SmtpOptions> smtp)
{
    private static readonly ConcurrentDictionary<int, Queue<DateTime>> Sent = new();

    private static string CleanAddress(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length is 0 or > 254 || s.Contains(',') || s.Contains(';') || s.Any(char.IsWhiteSpace)) throw new BusinessRuleException("Enter one valid email address.");
        try { var a = new MailAddress(s); if (a.Address != s || !a.Host.Contains('.')) throw new FormatException(); return s; }
        catch (FormatException) { throw new BusinessRuleException("That doesn't look like a valid email address."); }
    }

    private void Throttle(int userId)
    {
        var q = Sent.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = clock.UtcNow;
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
            if (q.Count >= smtp.Value.MaxPerUserPerHour) throw new BusinessRuleException("You've sent a lot of emails this hour. Try again a little later.");
            q.Enqueue(now);
        }
    }

    private async Task<string?> EmailOfAsync(int? customerId, CancellationToken ct) =>
        customerId is int id ? await db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.Email).SingleOrDefaultAsync(ct) : null;

    public async Task<EmailPreview> PreviewQuotationAsync(int id, string? to, string? note, CurrentUser user, CancellationToken ct)
    {
        var d = await docs.QuotationAsync(id, ct);
        return new EmailPreview(EmailTemplates.QuotationSubject(d), EmailTemplates.Quotation(d, note, user.FullName, d.Brand.Logo is { Length: > 0 }), to ?? d.Customer.Email);
    }

    public async Task<EmailSent> SendQuotationAsync(int id, string? to, string? note, CurrentUser user, CancellationToken ct)
    {
        Require();
        var d = await docs.QuotationAsync(id, ct);
        var address = CleanAddress(string.IsNullOrWhiteSpace(to) ? d.Customer.Email : to);
        Throttle(user.Id);
        var pdf = renderer.Quotation(d);
        var file = $"Quotation-{d.Number}.pdf";
        var msg = new EmailMessage(address, d.Customer.Name, EmailTemplates.QuotationSubject(d), EmailTemplates.Quotation(d, note, user.FullName, d.Brand.Logo is { Length: > 0 }),
            EmailTemplates.QuotationText(d, note, user.FullName), [new(file, pdf, "application/pdf")], d.Brand.Name, string.IsNullOrWhiteSpace(d.Brand.Email) ? null : d.Brand.Email, d.Brand.Logo);
        await sender.SendAsync(msg, ct);
        await AuditAsync(user, "QUOTATION_EMAILED", "Quotation", id.ToString(), $"{d.Number} to {address}", ct);
        return new EmailSent(address, msg.Subject, file);
    }

    private async Task<(PriceListDoc Doc, string? CustomerEmail)> PriceListAsync(int? customerId, string? tier, CancellationToken ct)
    {
        if (!company.HasPriceLists) throw new NotFoundException("Price list");
        Customer? c = customerId is int id ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer") : null;
        var t = tier is "Distributor" or "Wholesaler" or "Retailer" ? tier : c?.CustomerType switch { "Distributor" => "Distributor", "Wholesaler" => "Wholesaler", _ => "Retailer" };
        return (await docs.PriceListAsync(t, c?.Name, false, ct), c?.Email);
    }

    public async Task<EmailPreview> PreviewPriceListAsync(int? customerId, string? tier, string? to, string? note, CurrentUser user, CancellationToken ct)
    {
        var (d, mail) = await PriceListAsync(customerId, tier, ct);
        return new EmailPreview(EmailTemplates.PriceListSubject(d), EmailTemplates.PriceList(d, note, user.FullName, d.Brand.Logo is { Length: > 0 }), to ?? mail ?? "");
    }

    public async Task<EmailSent> SendPriceListAsync(int? customerId, string? tier, string? to, string? note, CurrentUser user, CancellationToken ct)
    {
        Require();
        var (d, mail) = await PriceListAsync(customerId, tier, ct);
        var address = CleanAddress(string.IsNullOrWhiteSpace(to) ? mail : to);
        Throttle(user.Id);
        var pdf = renderer.PriceList(d);
        var file = $"Price-list-{d.Reference}.pdf";
        var msg = new EmailMessage(address, d.CustomerName, EmailTemplates.PriceListSubject(d), EmailTemplates.PriceList(d, note, user.FullName, d.Brand.Logo is { Length: > 0 }),
            EmailTemplates.PriceListText(d, note, user.FullName), [new(file, pdf, "application/pdf")], d.Brand.Name, string.IsNullOrWhiteSpace(d.Brand.Email) ? null : d.Brand.Email, d.Brand.Logo);
        await sender.SendAsync(msg, ct);
        await AuditAsync(user, "PRICE_LIST_EMAILED", "PriceList", null, $"{d.Tier} list to {address}", ct);
        return new EmailSent(address, msg.Subject, file);
    }

    private void Require() { if (!sender.IsConfigured) throw new BusinessRuleException("Email isn't set up yet: the server has no Zoho mailbox configured."); }

    private async Task AuditAsync(CurrentUser u, string action, string entity, string? id, string detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog { UserId = u.Id, UserName = u.FullName, Action = action, Entity = entity, EntityId = id, At = clock.UtcNow, Detail = detail });
        await db.SaveChangesAsync(ct);
    }
}
