using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Payments;

/// <summary>
/// Makes the public "choose how to pay" link printed on receipts and quotations (e.g. https://chewypetsfeeds.com/pay/…). The link carries
/// an encrypted, tamper-proof reference to one document of one business — it can't be edited to reach someone else's invoice. Implemented
/// in the API (it needs the site address and the server's encryption keys). Null when the site address isn't configured.
/// </summary>
public interface IPayPageLinks
{
    string? UrlFor(string docType, int docId);
}

/// <param name="DocKind">"Receipt" or "Quotation" — what the customer is holding.</param>
/// <param name="Amount">What is owed right now (a receipt's unpaid balance, or an open quotation's total).</param>
/// <param name="Closed">Why nothing can be paid (paid in full, cancelled, already a sale), or null when payment is open.</param>
/// <param name="Awaiting">A payment was started on this document and hasn't been confirmed yet — it may just be on its way.</param>
public sealed record PayPageInfo(string Business, string DocKind, string DocNumber, string Customer, decimal Amount, string? Closed, bool Awaiting,
    IReadOnlyList<string> Providers);

/// <summary>What the payment page shows, and starting a checkout on the processor the customer picks.</summary>
public sealed class PayPageService(IBusinessDbContext db, PaymentLinkService pay, CompanyProfileService profile, ICompanyContext company)
{
    public async Task<PayPageInfo> GetAsync(string docType, int docId, CancellationToken ct)
    {
        var business = (await profile.EnsureAsync(ct)).LegalName is { Length: > 0 } n ? n : company.LegalName;
        var awaiting = await db.PaymentLinks.AsNoTracking().AnyAsync(l => l.DocType == docType && l.DocId == docId && l.Status == PaymentLinkStatuses.Pending, ct);
        if (docType == PaymentDocTypes.Invoice)
        {
            var i = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == docId, ct) ?? throw new NotFoundException("Receipt");
            var customer = await db.Customers.AsNoTracking().Where(c => c.Id == i.CustomerId).Select(c => c.Name).SingleAsync(ct);
            var owed = i.TotalAmount - i.AmountPaid;
            var closed = i.Status == PaymentStatuses.Voided ? "This sale was cancelled, so there is nothing to pay."
                       : owed <= 0 ? "This receipt is paid in full. Thank you!" : null;
            return new PayPageInfo(business, "Receipt", i.InvoiceNumber, customer, Math.Max(0, owed), closed, awaiting && closed is null, pay.Providers);
        }
        if (docType == PaymentDocTypes.Quotation)
        {
            var q = await db.Quotations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == docId, ct) ?? throw new NotFoundException("Quotation");
            var customer = await db.Customers.AsNoTracking().Where(c => c.Id == q.CustomerId).Select(c => c.Name).SingleAsync(ct);
            var closed = q.Status == QuotationStatuses.Converted ? "This quotation has already been paid for or turned into a sale."
                       : q.Status != QuotationStatuses.Open ? "This quotation is no longer open." : null;
            return new PayPageInfo(business, "Quotation", q.QuotationNumber, customer, q.TotalAmount, closed, awaiting && closed is null, pay.Providers);
        }
        throw new NotFoundException("Document");
    }

    /// <summary>A checkout on the chosen processor for what is owed right now. Returns the processor's page to send the customer to.</summary>
    public async Task<string> StartAsync(string docType, int docId, string provider, string? returnUrl, CancellationToken ct)
    {
        var info = await GetAsync(docType, docId, ct);
        if (info.Closed is not null) throw new BusinessRuleException(info.Closed);
        if (!info.Providers.Contains(provider)) throw new BusinessRuleException("That payment option isn't available right now. Please choose another.");
        var email = docType == PaymentDocTypes.Invoice
            ? await db.Invoices.AsNoTracking().Where(i => i.Id == docId).Join(db.Customers, i => i.CustomerId, c => c.Id, (i, c) => c.Email).SingleAsync(ct)
            : await db.Quotations.AsNoTracking().Where(q => q.Id == docId).Join(db.Customers, q => q.CustomerId, c => c.Id, (q, c) => c.Email).SingleAsync(ct);
        var link = await pay.GetOrCreateAsync(docType, docId, info.DocNumber, info.Amount, email, info.Customer, provider, ct, returnUrl)
                   ?? throw new BusinessRuleException("That payment option isn't available right now. Please choose another.");
        return link.Url;
    }

    /// <summary>Asks the processors about payments started on this document (the customer is back from paying). True if one was just recorded.</summary>
    public async Task<SettleResult?> ConfirmAsync(string docType, int docId, CancellationToken ct) => await pay.SettleDocumentAsync(docType, docId, ct);
}
