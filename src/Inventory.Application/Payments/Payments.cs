using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Sales;
using Inventory.Application.SalesAssistant;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Payments;

public sealed record GatewayCharge(string Url, string Reference);
public sealed record GatewayVerification(bool Success, long AmountKobo, string Currency, string? Channel);
public sealed record PayLink(string Url, decimal Amount, string Reference);
public sealed record SettleResult(bool Applied, bool AlreadySettled, string Message, int? InvoiceId = null, int? CustomerId = null, string? CustomerPhone = null);

/// <summary>The card / transfer / USSD processor. Implemented over Paystack in Infrastructure; the Application layer never sees a key.</summary>
public interface IPaymentGateway
{
    bool IsConfigured { get; }
    /// <summary>Paystack needs an email on every charge; when the customer has none this address is used so a link can still be made.</summary>
    string FallbackEmail { get; }
    Task<GatewayCharge> InitializeAsync(string email, long amountKobo, string reference, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);
    /// <summary>Asks the processor itself whether this reference was paid, and how much. A webhook is never trusted on its own word.</summary>
    Task<GatewayVerification?> VerifyAsync(string reference, CancellationToken ct);
    bool IsValidSignature(string rawBody, string? signature);
}

/// <summary>Tells the owner something happened (an invoice was paid). Email always; SMS when a provider is configured.</summary>
public interface IAdminNotifier
{
    Task NotifyAsync(string subject, string message, CancellationToken ct);
}

public static class PaymentDocTypes
{
    public const string Quotation = "Quotation";
    public const string Invoice = "Invoice";
}

/// <summary>
/// One online-payment link per document and amount. A quotation carries a link for its total and an invoice one for what is still owed on it,
/// printed as a clickable button in the PDF. When the customer pays, the processor calls us back; we confirm with the processor, record the
/// payment against the invoice exactly once, and tell the admin who paid.
/// </summary>
public sealed class PaymentLinkService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, IPaymentGateway gateway,
    IAdminNotifier notifier, CustomerPaymentService payments, QuotationService quotations)
{
    private static readonly TimeSpan Reuse = TimeSpan.FromHours(12);
    private static readonly CurrentUser System = new(0, "Paystack", "SYSTEM");

    public bool Enabled => gateway.IsConfigured;

    /// <summary>The link for this document at this exact amount, creating it if needed. Null when payments aren't set up or nothing is owed.</summary>
    public async Task<PayLink?> GetOrCreateAsync(string docType, int docId, string docNumber, decimal amount, string? customerEmail, string customerName, CancellationToken ct)
    {
        if (!gateway.IsConfigured || amount <= 0) return null;
        var cutoff = clock.UtcNow - Reuse;
        var existing = await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == docType && l.DocId == docId && l.Status == PaymentLinkStatuses.Pending && l.Amount == amount && l.CreatedAt > cutoff)
            .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new PayLink(existing.Url, existing.Amount, existing.Reference);

        var email = ValidEmail(customerEmail) ? customerEmail!.Trim() : gateway.FallbackEmail;
        var reference = $"{company.Key}-{docType[0]}{docId}-{clock.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        var charge = await gateway.InitializeAsync(email, (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), reference,
            new Dictionary<string, string> { ["company"] = company.Key, ["docType"] = docType, ["docId"] = docId.ToString(), ["docNumber"] = docNumber, ["customer"] = customerName }, ct);
        db.PaymentLinks.Add(new PaymentLink { Reference = reference, DocType = docType, DocId = docId, DocNumber = docNumber, CustomerName = customerName, Amount = amount, Url = charge.Url, Status = PaymentLinkStatuses.Pending, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        return new PayLink(charge.Url, amount, reference);
    }

    private static bool ValidEmail(string? e) => !string.IsNullOrWhiteSpace(e) && e.Contains('@') && e.Contains('.') && !e.Any(char.IsWhiteSpace);

    /// <summary>
    /// Called when the processor says a reference was paid. Confirms with the processor, then (in one transaction) marks the link paid and applies the
    /// money to the invoice. Safe to call any number of times: a second call finds the link already paid and does nothing.
    /// </summary>
    public async Task<SettleResult> SettleAsync(string reference, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Reference == reference, ct);
        if (link is null) return new SettleResult(false, false, "Unknown reference.");
        if (link.Status == PaymentLinkStatuses.Paid) return new SettleResult(false, true, "Already recorded.");

        var v = await gateway.VerifyAsync(reference, ct);
        if (v is null || !v.Success) return new SettleResult(false, false, "The processor does not show this payment as successful.");
        if (!string.Equals(v.Currency, "NGN", StringComparison.OrdinalIgnoreCase)) return new SettleResult(false, false, "Unexpected currency.");
        var paid = v.AmountKobo / 100m;

        string? summary = null; var already = false;
        int? invoiceId = null; int? custId = null; string? custPhone = null;
        await tx.RunAsync<bool>(async inner =>
        {
            var l = await db.PaymentLinks.FromSqlInterpolated($"SELECT * FROM payment_links WHERE Reference = {reference} FOR UPDATE").SingleAsync(inner);
            if (l.Status == PaymentLinkStatuses.Paid) { already = true; return false; }
            var note = "";
            if (l.DocType == PaymentDocTypes.Invoice)
            {
                var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(l.DocId, paid, "Paystack", System, inner);
                if (leftover > 0) note = $" ₦{leftover:N2} more than was owed on the invoice: refund or keep as credit.";
                if (applied == 0) note += " Nothing was owed on the invoice any more.";
                var inv = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == l.DocId, inner);
                invoiceId = inv.Id; custId = inv.CustomerId;
            }
            else
            {
                var quote = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == l.DocId, inner);
                var warehouseId = quote.WarehouseId ?? company.DefaultWarehouseId;
                if (warehouseId <= 0) throw new BusinessRuleException("No warehouse is configured for online sales (Companies:DefaultWarehouseId); the order could not be converted.");
                // "Card" is the closest of SaleRequestValidator's fixed PaymentMethods to an online gateway charge; the actual
                // processor ("Paystack") and channel (card/bank_transfer/ussd) are recorded on the Payment row and PaymentLink below.
                var sale = await quotations.ConvertWithinAsync(l.DocId, new ConvertQuoteRequest { WarehouseId = warehouseId, PaymentMethod = "Card", PaidNow = 0 }, System, inner);
                var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(sale.InvoiceId, paid, "Paystack", System, inner);
                if (leftover > 0) note = $" ₦{leftover:N2} more than the total: refund or keep as credit.";
                note += " Converted to a sale and stock has been deducted.";
                invoiceId = sale.InvoiceId; custId = quote.CustomerId;
            }
            var cust = custId is int cid ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid, inner) : null;
            custPhone = cust?.Phone;
            l.Status = PaymentLinkStatuses.Paid; l.PaidAt = clock.UtcNow; l.PaidAmount = paid; l.Channel = v.Channel;
            if (paid != l.Amount) note += $" (Link was for ₦{l.Amount:N2}.)";
            db.AuditLogs.Add(new AuditLog { UserId = null, UserName = "Paystack", Action = "ONLINE_PAYMENT", Entity = l.DocType, EntityId = l.DocId.ToString(), At = clock.UtcNow, Detail = $"{l.DocNumber} ₦{paid:N2} by {l.CustomerName} via {v.Channel ?? "Paystack"}" });
            await db.SaveChangesAsync(inner);
            var waLink = string.IsNullOrWhiteSpace(custPhone) ? "" : $" Chat: https://wa.me/{CustomerLookupService.Normalize(custPhone)}";
            summary = $"{(l.DocType == PaymentDocTypes.Invoice ? "Invoice" : "Quotation")} {l.DocNumber} has been paid: ₦{paid:N2} from {l.CustomerName}{(v.Channel is null ? "" : " by " + v.Channel)}.{note}{waLink}";
            return true;
        }, ct);

        if (already || summary is null) return new SettleResult(false, true, "Already recorded.");
        try
        {
            await notifier.NotifyAsync($"{company.LegalName}: payment received", summary, ct);
            await db.PaymentLinks.Where(l => l.Reference == reference).ExecuteUpdateAsync(u => u.SetProperty(l => l.NotifiedAt, clock.UtcNow), ct);
        }
        catch (Exception) { /* the payment is recorded; a failed message is retried by nobody, so it stays visible as NotifiedAt = null */ }
        return new SettleResult(true, false, summary, invoiceId, custId, custPhone);
    }
}
