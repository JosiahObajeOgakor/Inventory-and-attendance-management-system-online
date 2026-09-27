using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Sales;
using Inventory.Application.SalesAssistant;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Payments;

/// <param name="Reference">The processor's own reference for this charge (equal to ours for Paystack; AlatPay issues its own).</param>
public sealed record GatewayCharge(string Url, string Reference);
public sealed record GatewayVerification(bool Success, long AmountKobo, string Currency, string? Channel);
public sealed record PayLink(string Url, decimal Amount, string Reference, string Provider);
public sealed record SettleResult(bool Applied, bool AlreadySettled, string Message, int? InvoiceId = null, int? CustomerId = null, string? CustomerPhone = null,
    string? Provider = null, string? Reference = null);

/// <summary>A card / transfer / USSD processor (Paystack, AlatPay). Implemented in Infrastructure; the Application layer never sees a key.</summary>
public interface IPaymentGateway
{
    /// <summary>One of <see cref="PaymentProviders"/>.</summary>
    string Provider { get; }
    bool IsConfigured { get; }
    /// <summary>Processors need an email on every charge; when the customer has none this address is used so a link can still be made.</summary>
    string FallbackEmail { get; }
    Task<GatewayCharge> InitializeAsync(string email, long amountKobo, string reference, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);
    /// <summary>Asks the processor itself whether this (processor) reference was paid, and how much. A webhook is never trusted on its own word.</summary>
    Task<GatewayVerification?> VerifyAsync(string providerReference, CancellationToken ct);
    bool IsValidSignature(string rawBody, string? signature);
}

/// <summary>Every registered processor, looked up by name.</summary>
public sealed class PaymentGateways(IEnumerable<IPaymentGateway> all)
{
    private readonly IReadOnlyList<IPaymentGateway> list = all.ToList();

    public IPaymentGateway? Get(string? provider) => list.FirstOrDefault(g => g.Provider == provider);

    /// <summary>The configured processors, Paystack first (it's the one printed on documents).</summary>
    public IReadOnlyList<string> Configured =>
        list.Where(g => g.IsConfigured).Select(g => g.Provider).OrderBy(p => p == PaymentProviders.Paystack ? 0 : 1).ToList();

    public bool IsConfigured(string? provider) => Get(provider)?.IsConfigured == true;
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
/// payment against the invoice exactly once, and tell the admin who paid. Works the same for every processor (Paystack, AlatPay).
/// </summary>
public sealed class PaymentLinkService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, PaymentGateways gateways,
    IAdminNotifier notifier, CustomerPaymentService payments, QuotationService quotations)
{
    private static readonly TimeSpan Reuse = TimeSpan.FromHours(12);

    public bool Enabled => gateways.Configured.Count > 0;
    public IReadOnlyList<string> Providers => gateways.Configured;

    /// <summary>The link for this document at this exact amount on the default processor (Paystack when set up) — the one printed on documents.</summary>
    public Task<PayLink?> GetOrCreateAsync(string docType, int docId, string docNumber, decimal amount, string? customerEmail, string customerName, CancellationToken ct) =>
        GetOrCreateAsync(docType, docId, docNumber, amount, customerEmail, customerName, gateways.Configured.FirstOrDefault(), ct);

    /// <summary>The link for this document at this exact amount on the chosen processor, creating it if needed. Null when that processor isn't set up or nothing is owed.</summary>
    public async Task<PayLink?> GetOrCreateAsync(string docType, int docId, string docNumber, decimal amount, string? customerEmail, string customerName, string? provider, CancellationToken ct)
    {
        var gateway = gateways.Get(provider);
        if (gateway is null || !gateway.IsConfigured || amount <= 0) return null;
        var cutoff = clock.UtcNow - Reuse;
        var existing = await db.PaymentLinks.AsNoTracking()
            .Where(l => l.DocType == docType && l.DocId == docId && l.Provider == gateway.Provider && l.Status == PaymentLinkStatuses.Pending && l.Amount == amount && l.CreatedAt > cutoff)
            .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new PayLink(existing.Url, existing.Amount, existing.Reference, existing.Provider);

        var email = ValidEmail(customerEmail) ? customerEmail!.Trim() : gateway.FallbackEmail;
        var reference = $"{company.Key}-{docType[0]}{docId}-{clock.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        var charge = await gateway.InitializeAsync(email, (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), reference,
            new Dictionary<string, string> { ["company"] = company.Key, ["docType"] = docType, ["docId"] = docId.ToString(), ["docNumber"] = docNumber, ["customer"] = customerName }, ct);
        db.PaymentLinks.Add(new PaymentLink
        {
            Reference = reference, Provider = gateway.Provider, ProviderReference = charge.Reference, DocType = docType, DocId = docId, DocNumber = docNumber,
            CustomerName = customerName, Amount = amount, Url = charge.Url, Status = PaymentLinkStatuses.Pending, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return new PayLink(charge.Url, amount, reference, gateway.Provider);
    }

    private static bool ValidEmail(string? e) => !string.IsNullOrWhiteSpace(e) && e.Contains('@') && e.Contains('.') && !e.Any(char.IsWhiteSpace);

    /// <summary>
    /// Re-checks every still-pending link on this processor created since <paramref name="since"/> and settles the ones the processor shows as paid.
    /// Used when a processor's webhook can't name the link (AlatPay), and by the periodic safety net for missed webhooks.
    /// </summary>
    public async Task<IReadOnlyList<SettleResult>> SettlePendingAsync(string? provider, DateTime since, CancellationToken ct)
    {
        var refs = await db.PaymentLinks.AsNoTracking()
            .Where(l => l.Status == PaymentLinkStatuses.Pending && l.CreatedAt > since && (provider == null || l.Provider == provider))
            .OrderBy(l => l.Id).Select(l => l.Reference).Take(200).ToListAsync(ct);
        var applied = new List<SettleResult>();
        foreach (var r in refs)
        {
            var result = await SettleAsync(r, ct);
            if (result.Applied) applied.Add(result);
        }
        return applied;
    }

    /// <summary>Re-checks the pending links for one document (a customer saying "I've paid"). Returns the result that settled it, if any.</summary>
    public async Task<SettleResult?> SettleDocumentAsync(string docType, int docId, CancellationToken ct)
    {
        var refs = await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == docType && l.DocId == docId && l.Status == PaymentLinkStatuses.Pending)
            .OrderByDescending(l => l.Id).Select(l => l.Reference).ToListAsync(ct);
        foreach (var r in refs)
        {
            var result = await SettleAsync(r, ct);
            if (result.Applied) return result;
        }
        return null;
    }

    /// <summary>
    /// Called when a processor says a reference was paid. Confirms with that processor, then (in one transaction) marks the link paid and applies the
    /// money to the invoice. Safe to call any number of times: a second call finds the link already paid and does nothing.
    /// </summary>
    public async Task<SettleResult> SettleAsync(string reference, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Reference == reference, ct);
        if (link is null) return new SettleResult(false, false, "Unknown reference.");
        if (link.Status == PaymentLinkStatuses.Paid) return new SettleResult(false, true, "Already recorded.");

        var gateway = gateways.Get(link.Provider);
        if (gateway is null) return new SettleResult(false, false, "That payment processor isn't set up on this server.");
        var processor = PaymentProviders.DisplayName(link.Provider);
        var system = new CurrentUser(0, processor, "SYSTEM");
        var v = await gateway.VerifyAsync(link.ProviderReference ?? link.Reference, ct);
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
                var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(l.DocId, paid, processor, system, inner);
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
                // processor (Paystack/AlatPay) and channel (card/bank transfer/USSD) are recorded on the Payment row and PaymentLink below.
                var sale = await quotations.ConvertWithinAsync(l.DocId, new ConvertQuoteRequest { WarehouseId = warehouseId, PaymentMethod = "Card", PaidNow = 0 }, system, inner);
                var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(sale.InvoiceId, paid, processor, system, inner);
                if (leftover > 0) note = $" ₦{leftover:N2} more than the total: refund or keep as credit.";
                note += " Converted to a sale and stock has been deducted.";
                invoiceId = sale.InvoiceId; custId = quote.CustomerId;
            }
            var cust = custId is int cid ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid, inner) : null;
            custPhone = cust?.Phone;
            l.Status = PaymentLinkStatuses.Paid; l.PaidAt = clock.UtcNow; l.PaidAmount = paid; l.Channel = v.Channel;
            if (paid != l.Amount) note += $" (Link was for ₦{l.Amount:N2}.)";
            db.AuditLogs.Add(new AuditLog { UserId = null, UserName = processor, Action = "ONLINE_PAYMENT", Entity = l.DocType, EntityId = l.DocId.ToString(), At = clock.UtcNow, Detail = $"{l.DocNumber} ₦{paid:N2} by {l.CustomerName} via {processor}{(v.Channel is null ? "" : " (" + v.Channel + ")")}" });
            await db.SaveChangesAsync(inner);
            var waLink = string.IsNullOrWhiteSpace(custPhone) ? "" : $" Chat: https://wa.me/{CustomerLookupService.Normalize(custPhone)}";
            summary = $"{(l.DocType == PaymentDocTypes.Invoice ? "Invoice" : "Quotation")} {l.DocNumber} has been paid: ₦{paid:N2} from {l.CustomerName} via {processor}{(v.Channel is null ? "" : " (" + v.Channel + ")")}.{note}{waLink}";
            return true;
        }, ct);

        if (already || summary is null) return new SettleResult(false, true, "Already recorded.");
        try
        {
            await notifier.NotifyAsync($"{company.LegalName}: payment received", summary, ct);
            await db.PaymentLinks.Where(l => l.Reference == reference).ExecuteUpdateAsync(u => u.SetProperty(l => l.NotifiedAt, clock.UtcNow), ct);
        }
        catch (Exception) { /* the payment is recorded; a failed message is retried by nobody, so it stays visible as NotifiedAt = null */ }
        return new SettleResult(true, false, summary, invoiceId, custId, custPhone, link.Provider, reference);
    }
}
