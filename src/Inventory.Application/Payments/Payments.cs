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
public sealed record SettleResult(bool Applied, bool AlreadySettled, string Message, int? InvoiceId = null, int? CustomerId = null, string? CustomerPhone = null,
    string? Reference = null, decimal? Shortfall = null, bool Unfulfilled = false, decimal? Paid = null, string? InvoiceNumber = null);

public sealed record VirtualAccount(string AccountNumber, string BankName, string TransactionId, DateTime ExpiresAt);
/// <summary>What the processor says about one bank-transfer attempt. AmountSent is what actually arrived (null until something did).</summary>
public sealed record TransferStatus(bool Completed, decimal Amount, decimal? AmountSent, string? RawStatus);
public sealed record PayAccount(string AccountNumber, string BankName, decimal Amount, DateTime ExpiresAt, string Reference);

/// <summary>A single-use bank account number shown inside the chat (AlatPay). Unlike <see cref="IPaymentGateway"/> nothing leaves the conversation.</summary>
public interface IVirtualAccountGateway
{
    bool IsConfigured { get; }
    Task<VirtualAccount> CreateAsync(string orderId, decimal amount, string description, string firstName, string lastName, string email, string? phone, CancellationToken ct);
    /// <summary>Asks the processor itself whether this attempt was paid. A webhook is never trusted on its own word.</summary>
    Task<TransferStatus?> VerifyAsync(string transactionId, CancellationToken ct);
    bool IsValidSignature(string rawBody, string? signature);
}

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
/// The chat assistant uses the same table with <see cref="AlatProvider"/>: instead of a URL the customer gets a single-use bank account number inside WhatsApp.
/// </summary>
public sealed class PaymentLinkService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, IPaymentGateway gateway,
    IAdminNotifier notifier, CustomerPaymentService payments, QuotationService quotations, IVirtualAccountGateway? virtualAccounts = null)
{
    public const string AlatProvider = "AlatPay";
    private static readonly TimeSpan Reuse = TimeSpan.FromHours(12);

    public bool Enabled => gateway.IsConfigured;
    public bool VirtualAccountsEnabled => virtualAccounts is { IsConfigured: true };

    /// <summary>The link for this document at this exact amount, creating it if needed. Null when payments aren't set up or nothing is owed.</summary>
    public async Task<PayLink?> GetOrCreateAsync(string docType, int docId, string docNumber, decimal amount, string? customerEmail, string customerName, CancellationToken ct)
    {
        if (!gateway.IsConfigured || amount <= 0) return null;
        var cutoff = clock.UtcNow - Reuse;
        var existing = await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == docType && l.DocId == docId && l.Status == PaymentLinkStatuses.Pending && l.Provider == "Paystack" && l.Amount == amount && l.CreatedAt > cutoff)
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

    /// <summary>
    /// A single-use bank account for exactly this quotation total, reused while it is still valid so a customer who asks again gets the SAME account
    /// (never two live accounts for one order). Null when AlatPay isn't configured or nothing is owed.
    /// </summary>
    public async Task<PayAccount?> GetOrCreateVirtualAccountAsync(int quotationId, string quotationNumber, decimal amount, string customerName, string? phone, string? email, CancellationToken ct)
    {
        if (virtualAccounts is not { IsConfigured: true } || amount <= 0) return null;
        var now = clock.UtcNow;
        var existing = await db.PaymentLinks.AsNoTracking()
            .Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == quotationId && l.Provider == AlatProvider && l.Status == PaymentLinkStatuses.Pending && l.Amount == amount && l.ExpiresAt > now.AddMinutes(5))
            .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new PayAccount(existing.AccountNumber!, existing.BankName ?? "", existing.Amount, existing.ExpiresAt!.Value, existing.Reference);

        // The reference doubles as AlatPay's orderId and starts with the company key, so the webhook can be routed to the right business.
        var reference = $"{company.Key}-Q{quotationId}-{now:yyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        var parts = (string.IsNullOrWhiteSpace(customerName) ? "Customer" : customerName.Trim()).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0]; var last = parts.Length > 1 ? parts[1] : parts[0];
        var mail = ValidEmail(email) ? email!.Trim() : gateway.FallbackEmail;
        var account = await virtualAccounts.CreateAsync(reference, amount, $"{company.LegalName} order {quotationNumber}", first, last, mail, phone, ct);
        db.PaymentLinks.Add(new PaymentLink
        {
            Reference = reference, DocType = PaymentDocTypes.Quotation, DocId = quotationId, DocNumber = quotationNumber, CustomerName = customerName, Amount = amount,
            Url = "", Status = PaymentLinkStatuses.Pending, CreatedAt = now, Provider = AlatProvider, ExternalId = account.TransactionId,
            AccountNumber = account.AccountNumber, BankName = account.BankName, ExpiresAt = account.ExpiresAt,
        });
        await db.SaveChangesAsync(ct);
        return new PayAccount(account.AccountNumber, account.BankName, amount, account.ExpiresAt, reference);
    }

    /// <summary>For a bank transfer into an AlatPay virtual account: that account (bank, account name, number) is the one the money arrived in. Paystack card/link payments have none.</summary>
    private AccountRef? ReceivingAccount(PaymentLink l) =>
        l.Provider == AlatProvider && !string.IsNullOrEmpty(l.AccountNumber) ? new AccountRef(l.BankName ?? "AlatPay", company.LegalName, l.AccountNumber) : null;

    private static bool ValidEmail(string? e) => !string.IsNullOrWhiteSpace(e) && e.Contains('@') && e.Contains('.') && !e.Any(char.IsWhiteSpace);

    /// <summary>
    /// Called when the processor says a reference was paid. Confirms with the processor, then (in one transaction) marks the link paid and applies the
    /// money to the invoice. Safe to call any number of times: a second call finds the link already paid and does nothing.
    /// </summary>
    public async Task<SettleResult> SettleAsync(string reference, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Reference == reference, ct);
        if (link is null) return new SettleResult(false, false, "Unknown reference.");
        if (link.Status is PaymentLinkStatuses.Paid or PaymentLinkStatuses.PaidUnfulfilled) return new SettleResult(false, true, "Already recorded.", Reference: reference);

        GatewayVerification? v;
        if (link.Provider == AlatProvider)
        {
            if (virtualAccounts is null || string.IsNullOrEmpty(link.ExternalId)) return new SettleResult(false, false, "No processor reference to check.", Reference: reference);
            var t = await virtualAccounts.VerifyAsync(link.ExternalId, ct);
            if (t is null || !t.Completed) return new SettleResult(false, false, "The processor does not show this payment as successful.", Reference: reference);
            var sent = t.AmountSent ?? t.Amount;
            if (sent < link.Amount) return new SettleResult(false, false, $"Underpaid: ₦{sent:N2} of ₦{link.Amount:N2}.", Reference: reference, Shortfall: link.Amount - sent, Paid: sent);
            v = new GatewayVerification(true, (long)Math.Round(sent * 100m, MidpointRounding.AwayFromZero), "NGN", "bank_transfer");
        }
        else v = await gateway.VerifyAsync(reference, ct);
        if (v is null || !v.Success) return new SettleResult(false, false, "The processor does not show this payment as successful.", Reference: reference);
        if (!string.Equals(v.Currency, "NGN", StringComparison.OrdinalIgnoreCase)) return new SettleResult(false, false, "Unexpected currency.", Reference: reference);
        var paid = v.AmountKobo / 100m;
        var system = new CurrentUser(0, link.Provider, "SYSTEM");

        string? summary = null; var already = false;
        int? invoiceId = null; int? custId = null; string? custPhone = null; string? invoiceNumber = null;
        try
        {
            await tx.RunAsync<bool>(async inner =>
            {
                var l = await db.PaymentLinks.FromSqlInterpolated($"SELECT * FROM payment_links WHERE Reference = {reference} FOR UPDATE").SingleAsync(inner);
                if (l.Status != PaymentLinkStatuses.Pending) { already = true; return false; }
                var note = "";
                if (l.DocType == PaymentDocTypes.Invoice)
                {
                    var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(l.DocId, paid, l.Provider, system, inner, ReceivingAccount(l));
                    if (leftover > 0) note = $" ₦{leftover:N2} more than was owed on the invoice: refund or keep as credit.";
                    if (applied == 0) note += " Nothing was owed on the invoice any more.";
                    var inv = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == l.DocId, inner);
                    invoiceId = inv.Id; custId = inv.CustomerId; invoiceNumber = inv.InvoiceNumber;
                }
                else
                {
                    var quote = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == l.DocId, inner);
                    var warehouseId = quote.WarehouseId ?? await SalesAssistant.Chat.WarehouseChooser.ForOrderAsync(db, company, clock,
                        (await db.QuotationItems.AsNoTracking().Where(i => i.QuotationId == quote.Id).Select(i => new { i.ProductId, i.Quantity }).ToListAsync(inner)).Select(i => (i.ProductId, i.Quantity)).ToList(), inner);
                    if (warehouseId <= 0) throw new BusinessRuleException("This business has no warehouse yet, so the online order could not be converted.");
                    // "Card" is the closest of SaleRequestValidator's fixed PaymentMethods to an online gateway charge; the actual
                    // processor and channel are recorded on the Payment row and PaymentLink below.
                    var sale = await quotations.ConvertWithinAsync(l.DocId, new ConvertQuoteRequest { WarehouseId = warehouseId, PaymentMethod = l.Provider == AlatProvider ? "Bank Transfer" : "Card", PaidNow = 0 }, system, inner);
                    var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(sale.InvoiceId, paid, l.Provider, system, inner, ReceivingAccount(l));
                    if (leftover > 0) note = $" ₦{leftover:N2} more than the total: refund or keep as credit.";
                    note += " Converted to a sale and stock has been deducted.";
                    invoiceId = sale.InvoiceId; custId = quote.CustomerId; invoiceNumber = sale.InvoiceNumber;
                }
                var cust = custId is int cid ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid, inner) : null;
                custPhone = cust?.Phone;
                l.Status = PaymentLinkStatuses.Paid; l.PaidAt = clock.UtcNow; l.PaidAmount = paid; l.Channel = v.Channel;
                if (paid != l.Amount) note += $" (Link was for ₦{l.Amount:N2}.)";
                db.AuditLogs.Add(new AuditLog { UserId = null, UserName = l.Provider, Action = "ONLINE_PAYMENT", Entity = l.DocType, EntityId = l.DocId.ToString(), At = clock.UtcNow, Detail = $"{l.DocNumber} ₦{paid:N2} by {l.CustomerName} via {v.Channel ?? l.Provider}" });
                await db.SaveChangesAsync(inner);
                var waLink = string.IsNullOrWhiteSpace(custPhone) ? "" : $" Chat: https://wa.me/{CustomerLookupService.Normalize(custPhone)}";
                summary = $"{(l.DocType == PaymentDocTypes.Invoice ? "Invoice" : "Quotation")} {l.DocNumber} has been paid: ₦{paid:N2} from {l.CustomerName}{(v.Channel is null ? "" : " by " + v.Channel)}.{note}{waLink}";
                return true;
            }, ct);
        }
        catch (BusinessRuleException ex) when (link.Provider == AlatProvider && link.DocType == PaymentDocTypes.Quotation)
        {
            // The customer's money is already with the processor but the sale can't be completed (typically the stock went after the reservation lapsed).
            // Never lose track of it: park the link where a person will see it, and say so loudly.
            db.ClearTracker();
            await db.PaymentLinks.Where(l => l.Reference == reference && l.Status == PaymentLinkStatuses.Pending).ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, PaymentLinkStatuses.PaidUnfulfilled).SetProperty(l => l.PaidAt, clock.UtcNow).SetProperty(l => l.PaidAmount, paid).SetProperty(l => l.Channel, v.Channel), ct);
            var quote = await db.Quotations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == link.DocId, ct);
            var customer = quote is null ? null : await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == quote.CustomerId, ct);
            db.AuditLogs.Add(new AuditLog { UserId = null, UserName = link.Provider, Action = "ONLINE_PAYMENT_UNFULFILLED", Entity = link.DocType, EntityId = link.DocId.ToString(), At = clock.UtcNow, Detail = $"{link.DocNumber} ₦{paid:N2} from {link.CustomerName}: {ex.Message}" });
            await db.SaveChangesAsync(ct);
            try { await notifier.NotifyAsync($"{company.LegalName}: PAID BUT NOT FULFILLED", $"Quotation {link.DocNumber}: ₦{paid:N2} was received from {link.CustomerName} but the sale could not be completed ({ex.Message}). Refund or fulfil by hand. Chat: https://wa.me/{CustomerLookupService.Normalize(customer?.Phone ?? "")}", ct); }
            catch (Exception) { /* the parked status above is the durable record */ }
            return new SettleResult(false, false, ex.Message, null, customer?.Id, customer?.Phone, reference, null, true, paid);
        }

        if (already || summary is null) return new SettleResult(false, true, "Already recorded.", Reference: reference);
        try
        {
            await notifier.NotifyAsync($"{company.LegalName}: payment received", summary, ct);
            await db.PaymentLinks.Where(l => l.Reference == reference).ExecuteUpdateAsync(u => u.SetProperty(l => l.NotifiedAt, clock.UtcNow), ct);
        }
        catch (Exception) { /* the payment is recorded; a failed message is retried by nobody, so it stays visible as NotifiedAt = null */ }
        return new SettleResult(true, false, summary, invoiceId, custId, custPhone, reference, null, false, paid, invoiceNumber);
    }
}