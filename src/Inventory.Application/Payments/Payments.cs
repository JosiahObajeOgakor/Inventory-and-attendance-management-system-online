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
    string? Provider = null, string? Reference = null, decimal? Shortfall = null, bool Unfulfilled = false, decimal? Paid = null, string? InvoiceNumber = null);

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
    /// <summary>Money a trade partner pays into their wallet (DocId is the partner); settled into a confirmed wallet entry, never a sale.</summary>
    public const string Wallet = "Wallet";
}

/// <summary>
/// One online-payment link per document and amount. A quotation carries a link for its total and an invoice one for what is still owed on it,
/// printed as a clickable button in the PDF. When the customer pays, the processor calls us back; we confirm with the processor, record the
/// payment against the invoice exactly once, and tell the admin who paid. Works the same for every processor (Paystack, AlatPay).
/// The chat assistant also uses this table for AlatPay's single-use bank accounts (<see cref="PaymentLink.ExternalId"/> set): instead of a URL the customer
/// gets an account number inside WhatsApp.
/// </summary>
public sealed class PaymentLinkService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, PaymentGateways gateways,
    IAdminNotifier notifier, CustomerPaymentService payments, QuotationService quotations, IVirtualAccountGateway? virtualAccounts = null, ISaleAlerts? alerts = null,
    Partners.IStaffAlerts? staff = null)
{
    /// <summary>The AlatPay provider name as stored on links (same constant the payment pages use).</summary>
    public const string AlatProvider = PaymentProviders.AlatPay;
    private static readonly TimeSpan Reuse = TimeSpan.FromHours(12);
    /// <summary>Metadata key a gateway reads as "send the customer back here after paying"; it is not stored on the processor's side.</summary>
    public const string ReturnUrlKey = "return_url";

    public bool Enabled => Providers.Count > 0;
    public bool VirtualAccountsEnabled => virtualAccounts is { IsConfigured: true };

    /// <summary>
    /// The processors this business can take payment through: set up on the server AND allowed for the business (Companies:PaymentProviders).
    /// Both businesses share the AlatPay account; Paystack is ChewyPets' account, so Candid Purrfect doesn't offer it until it has its own.
    /// Everything that offers a way to pay — the pay page, the QR code, the PDF button, the sales assistant — reads this list.
    /// </summary>
    public IReadOnlyList<string> Providers =>
        gateways.Configured.Where(p => company.PaymentProviders is not { } allowed || allowed.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>The link for this document at this exact amount on the business's default processor — the fallback printed on documents.</summary>
    public Task<PayLink?> GetOrCreateAsync(string docType, int docId, string docNumber, decimal amount, string? customerEmail, string customerName, CancellationToken ct) =>
        GetOrCreateAsync(docType, docId, docNumber, amount, customerEmail, customerName, Providers.FirstOrDefault(), ct);

    /// <summary>The link for this document at this exact amount on the chosen processor, creating it if needed. Null when that processor isn't set up or nothing is owed.</summary>
    public async Task<PayLink?> GetOrCreateAsync(string docType, int docId, string docNumber, decimal amount, string? customerEmail, string customerName, string? provider, CancellationToken ct,
        string? returnUrl = null)
    {
        var gateway = gateways.Get(provider);
        if (gateway is null || !gateway.IsConfigured || amount <= 0 || !Providers.Contains(gateway.Provider)) return null;   // not offered by this business
        var cutoff = clock.UtcNow - Reuse;
        var existing = await db.PaymentLinks.AsNoTracking()
            .Where(l => l.DocType == docType && l.DocId == docId && l.Provider == gateway.Provider && l.ExternalId == null && l.Status == PaymentLinkStatuses.Pending && l.Amount == amount && l.CreatedAt > cutoff)
            .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new PayLink(existing.Url, existing.Amount, existing.Reference, existing.Provider);

        var email = ValidEmail(customerEmail) ? customerEmail!.Trim() : gateway.FallbackEmail;
        var reference = $"{company.Key}-{docType[0]}{docId}-{clock.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        // Both businesses may share one processor account, so every charge says which business it is for (Paystack shows these on the transaction).
        var metadata = new Dictionary<string, string>
        {
            ["company"] = company.Key, ["business"] = company.LegalName, ["docType"] = docType, ["docId"] = docId.ToString(), ["docNumber"] = docNumber, ["customer"] = customerName,
        };
        if (!string.IsNullOrWhiteSpace(returnUrl)) metadata[ReturnUrlKey] = returnUrl;
        var charge = await gateway.InitializeAsync(email, (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), reference, metadata, ct);
        db.PaymentLinks.Add(new PaymentLink
        {
            Reference = reference, Provider = gateway.Provider, ProviderReference = charge.Reference, DocType = docType, DocId = docId, DocNumber = docNumber,
            CustomerName = customerName, Amount = amount, Url = charge.Url, Status = PaymentLinkStatuses.Pending, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return new PayLink(charge.Url, amount, reference, gateway.Provider);
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
            .Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == quotationId && l.ExternalId != null && l.Status == PaymentLinkStatuses.Pending && l.Amount == amount && l.ExpiresAt > now.AddMinutes(5))
            .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new PayAccount(existing.AccountNumber!, existing.BankName ?? "", existing.Amount, existing.ExpiresAt!.Value, existing.Reference);

        // The reference doubles as AlatPay's orderId and starts with the company key, so the webhook can be routed to the right business.
        var reference = $"{company.Key}-Q{quotationId}-{now:yyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        var parts = (string.IsNullOrWhiteSpace(customerName) ? "Customer" : customerName.Trim()).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0]; var last = parts.Length > 1 ? parts[1] : parts[0];
        var mail = ValidEmail(email) ? email!.Trim() : (gateways.Get(PaymentProviders.Paystack) ?? gateways.Get(PaymentProviders.AlatPay))?.FallbackEmail ?? "payments@invalid.example";
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

    /// <summary>Works out who bought what and where the stock came from, then lets <see cref="ISaleAlerts"/> tell the people who need to know. Also counts the completed sale for the dashboard.</summary>
    private async Task AlertAsync(PaymentLink link, int invoiceId, decimal paid, string? channel, CancellationToken ct)
    {
        var inv = await db.Invoices.AsNoTracking().Include(i => i.Items).SingleAsync(i => i.Id == invoiceId, ct);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == inv.CustomerId, ct);
        var names = await db.Products.AsNoTracking().Where(p => inv.Items.Select(i => i.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var fromWarehouses = await (from m in db.StockMovements.AsNoTracking() join w in db.Warehouses.AsNoTracking() on m.WarehouseId equals w.Id
                                    where m.ReferenceType == MovementReferences.Invoice && m.ReferenceId == invoiceId && m.MovementType == MovementTypes.Out
                                    group m by w.Name into g select new { g.Key, Qty = g.Sum(x => x.Quantity) }).ToListAsync(ct);
        var branch = inv.WarehouseId is int wid ? await (from w in db.Warehouses.AsNoTracking() join b in db.Branches.AsNoTracking() on w.BranchId equals b.Id where w.Id == wid select b.Name).FirstOrDefaultAsync(ct) : null;
        var conv = link.DocType == PaymentDocTypes.Quotation ? await db.ChatConversations.AsNoTracking().FirstOrDefaultAsync(c => c.QuotationId == link.DocId, ct) : null;
        var via = conv is null ? "Payment link" : conv.Channel == ChatChannels.WhatsApp ? "WhatsApp" : "Website chat";
        var into = !string.IsNullOrEmpty(link.ExternalId) && !string.IsNullOrEmpty(link.AccountNumber) ? $"{link.BankName} {link.AccountNumber}" : PaymentProviders.DisplayName(link.Provider);
        if (conv is not null)
        {
            db.ChatEvents.Add(new ChatEvent { At = clock.UtcNow, Channel = conv.Channel, Kind = ChatEventKinds.Sale, ConversationId = conv.Id, CustomerId = customer.Id, Amount = inv.TotalAmount, Reference = inv.InvoiceNumber });
            await db.SaveChangesAsync(ct);
        }
        var alert = new SaleAlert(company.LegalName, inv.Id, inv.InvoiceNumber, via, customer.Name, customer.Phone, customer.Email, customer.CustomerType,
            inv.Items.Select(i => new SaleAlertLine(names.GetValueOrDefault(i.ProductId, "Item"), i.Quantity, i.UnitPrice, i.LineTotal)).ToList(), inv.TotalAmount, paid,
            channel switch { "bank_transfer" => "bank transfer", null or "" => link.Provider, var c => c.Replace('_', ' ') }, into,
            fromWarehouses.Select(f => $"{f.Key} ({f.Qty})").ToList(), branch, clock.UtcNow);
        await alerts!.SaleCompletedAsync(alert, ct);
    }

    /// <summary>A top-up confirmed: the partner hears on WhatsApp, SMS and email; the admin on WhatsApp, SMS and email, with the clerks copied.</summary>
    private async Task WalletToldAsync(int partnerId, decimal paid, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == partnerId, ct);
        if (p is null) return;
        var balance = await db.WalletEntries.Where(w => w.TradePartnerId == p.Id && w.Status == WalletEntryStatuses.Confirmed).SumAsync(w => (decimal?)w.Amount, ct) ?? 0;
        if (staff is null) { await notifier.NotifyAsync($"{company.LegalName}: wallet top-up", $"{p.BusinessName} topped up ₦{paid:N2}. Wallet: ₦{balance:N2}.", ct); return; }
        await staff.TellStaffAsync(new Partners.StaffAlert($"{company.LegalName}: {p.BusinessName} topped up ₦{paid:N2}",
            $"💰 WALLET TOP-UP CONFIRMED — {company.LegalName}\n{p.BusinessName} ({p.ApprovedTier}) paid ₦{paid:N2} into their wallet (AlatPay).\nWallet balance: ₦{balance:N2}."), ct);
        await staff.TellContactAsync(new Partners.ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: ₦{paid:N2} added to your wallet",
            $"Payment received. ₦{paid:N2} has been added to your {company.LegalName} wallet.\n\nYour balance is now ₦{balance:N2}. You can order from your partner account at your {p.ApprovedTier?.ToLowerInvariant()} prices and pay from your wallet in one tap."), ct);
    }

    /// <summary>For a bank transfer into an AlatPay virtual account: that account (bank, account name, number) is the one the money arrived in. Paystack card/link payments have none.</summary>
    private AccountRef? ReceivingAccount(PaymentLink l) =>
        !string.IsNullOrEmpty(l.ExternalId) && !string.IsNullOrEmpty(l.AccountNumber) ? new AccountRef(l.BankName ?? "AlatPay", company.LegalName, l.AccountNumber) : null;

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
        if (link.Status is PaymentLinkStatuses.Paid or PaymentLinkStatuses.PaidUnfulfilled) return new SettleResult(false, true, "Already recorded.", Reference: reference);

        var processor = PaymentProviders.DisplayName(link.Provider);
        GatewayVerification? v;
        if (!string.IsNullOrEmpty(link.ExternalId))
        {
            // The chat's single-use bank account (AlatPay virtual account): ask AlatPay what actually arrived on that transfer.
            if (virtualAccounts is null) return new SettleResult(false, false, "No processor to check this bank transfer with.", Reference: reference);
            var t = await virtualAccounts.VerifyAsync(link.ExternalId, ct);
            if (t is null || !t.Completed) return new SettleResult(false, false, "The processor does not show this payment as successful.", Reference: reference);
            var sent = t.AmountSent ?? t.Amount;
            if (sent < link.Amount) return new SettleResult(false, false, $"Underpaid: ₦{sent:N2} of ₦{link.Amount:N2}.", Reference: reference, Shortfall: link.Amount - sent, Paid: sent);
            v = new GatewayVerification(true, (long)Math.Round(sent * 100m, MidpointRounding.AwayFromZero), "NGN", "bank_transfer");
        }
        else
        {
            var gateway = gateways.Get(link.Provider);
            if (gateway is null) return new SettleResult(false, false, "That payment processor isn't set up on this server.", Reference: reference);
            v = await gateway.VerifyAsync(link.ProviderReference ?? link.Reference, ct);
        }
        if (v is null || !v.Success) return new SettleResult(false, false, "The processor does not show this payment as successful.", Reference: reference);
        if (!string.Equals(v.Currency, "NGN", StringComparison.OrdinalIgnoreCase)) return new SettleResult(false, false, "Unexpected currency.", Reference: reference);
        var paid = v.AmountKobo / 100m;
        var system = new CurrentUser(0, processor, "SYSTEM");

        // Paid on a link whose sale was deleted: there is nothing to attach it to, so it's recorded on the link (once) and the admin is told to refund it.
        if (link.Status == PaymentLinkStatuses.Cancelled)
        {
            var first = await tx.RunAsync(async inner =>
            {
                var l = await db.PaymentLinks.FromSqlInterpolated($"SELECT * FROM payment_links WHERE Reference = {reference} FOR UPDATE").SingleAsync(inner);
                if (l.PaidAt is not null) return false;
                l.PaidAt = clock.UtcNow; l.PaidAmount = paid; l.Channel = v.Channel;
                db.AuditLogs.Add(new AuditLog { UserId = null, UserName = processor, Action = "PAYMENT_ON_DELETED_SALE", Entity = l.DocType, EntityId = l.DocId.ToString(), At = clock.UtcNow,
                    Detail = $"{l.DocNumber} ₦{paid:N2} by {l.CustomerName} via {processor} — the sale had been deleted; refund needed" });
                await db.SaveChangesAsync(inner);
                return true;
            }, ct);
            if (first)
                try { await notifier.NotifyAsync($"{company.LegalName}: refund needed", $"₦{paid:N2} was paid via {processor} by {link.CustomerName} for {link.DocNumber}, which had been deleted. Please refund it from your {processor} dashboard.", ct); }
                catch (Exception) { /* recorded in the activity log above */ }
            return new SettleResult(false, true, "Paid on a deleted sale — refund needed.");
        }

        string? summary = null; var already = false;
        int? invoiceId = null; int? custId = null; string? custPhone = null; string? invoiceNumber = null; int? walletPartner = null;
        try
        {
            await tx.RunAsync<bool>(async inner =>
            {
                var l = await db.PaymentLinks.FromSqlInterpolated($"SELECT * FROM payment_links WHERE Reference = {reference} FOR UPDATE").SingleAsync(inner);
                if (l.Status != PaymentLinkStatuses.Pending) { already = true; return false; }
                var note = "";
                if (l.DocType == PaymentDocTypes.Wallet)
                {
                    // a partner's top-up: the pending wallet entry becomes money they can spend, exactly once (the entry's reference is unique)
                    var w = await db.WalletEntries.FromSqlInterpolated($"SELECT * FROM wallet_entries WHERE Reference = {reference} FOR UPDATE").SingleOrDefaultAsync(inner);
                    if (w is null)
                    {
                        w = new WalletEntry { TradePartnerId = l.DocId, Kind = WalletEntryKinds.Funding, Reference = reference, Method = "alatpay", CreatedAt = clock.UtcNow };
                        db.WalletEntries.Add(w);
                    }
                    if (w.Status == WalletEntryStatuses.Confirmed) { already = true; return false; }
                    w.Amount = paid; w.Status = WalletEntryStatuses.Confirmed; w.ConfirmedAt = clock.UtcNow; w.ConfirmedBy = processor; w.PaymentLinkId = l.Id;
                    l.Status = PaymentLinkStatuses.Paid; l.PaidAt = clock.UtcNow; l.PaidAmount = paid; l.Channel = v.Channel;
                    db.AuditLogs.Add(new AuditLog { UserId = null, UserName = processor, Action = "WALLET_FUNDED", Entity = "TradePartner", EntityId = l.DocId.ToString(), At = clock.UtcNow, Detail = $"₦{paid:N2} by {l.CustomerName} via {processor}" });
                    await db.SaveChangesAsync(inner);
                    walletPartner = l.DocId;
                    summary = $"Wallet top-up: ₦{paid:N2} from {l.CustomerName} via {processor}{(v.Channel is null ? "" : " (" + v.Channel + ")")}.";
                    return true;
                }
                if (l.DocType == PaymentDocTypes.Invoice)
                {
                    var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(l.DocId, paid, processor, system, inner, ReceivingAccount(l));
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
                    var sale = await quotations.ConvertWithinAsync(l.DocId, new ConvertQuoteRequest { WarehouseId = warehouseId, PaymentMethod = !string.IsNullOrEmpty(l.ExternalId) ? "Bank Transfer" : "Card", PaidNow = 0 }, system, inner);
                    var (applied, leftover) = await payments.ApplyToInvoiceWithinAsync(sale.InvoiceId, paid, processor, system, inner, ReceivingAccount(l));
                    if (leftover > 0) note = $" ₦{leftover:N2} more than the total: refund or keep as credit.";
                    note += " Converted to a sale and stock has been deducted.";
                    invoiceId = sale.InvoiceId; custId = quote.CustomerId; invoiceNumber = sale.InvoiceNumber;
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
        }
        catch (BusinessRuleException ex) when (link.DocType == PaymentDocTypes.Quotation)
        {
            // The customer's money is already with the processor but the sale can't be completed (typically the stock went after the reservation lapsed).
            // Never lose track of it: park the link where a person will see it, and say so loudly.
            db.ClearTracker();
            await db.PaymentLinks.Where(l => l.Reference == reference && l.Status == PaymentLinkStatuses.Pending).ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, PaymentLinkStatuses.PaidUnfulfilled).SetProperty(l => l.PaidAt, clock.UtcNow).SetProperty(l => l.PaidAmount, paid).SetProperty(l => l.Channel, v.Channel), ct);
            var quote = await db.Quotations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == link.DocId, ct);
            var customer = quote is null ? null : await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == quote.CustomerId, ct);
            db.AuditLogs.Add(new AuditLog { UserId = null, UserName = processor, Action = "ONLINE_PAYMENT_UNFULFILLED", Entity = link.DocType, EntityId = link.DocId.ToString(), At = clock.UtcNow, Detail = $"{link.DocNumber} ₦{paid:N2} from {link.CustomerName}: {ex.Message}" });
            await db.SaveChangesAsync(ct);
            try { await notifier.NotifyAsync($"{company.LegalName}: PAID BUT NOT FULFILLED", $"Quotation {link.DocNumber}: ₦{paid:N2} was received from {link.CustomerName} but the sale could not be completed ({ex.Message}). Refund or fulfil by hand. Chat: https://wa.me/{CustomerLookupService.Normalize(customer?.Phone ?? "")}", ct); }
            catch (Exception) { /* the parked status above is the durable record */ }
            return new SettleResult(false, false, ex.Message, null, customer?.Id, customer?.Phone, link.Provider, reference, null, true, paid);
        }

        if (already || summary is null) return new SettleResult(false, true, "Already recorded.", Reference: reference);
        try
        {
            if (walletPartner is int wp) await WalletToldAsync(wp, paid, ct);
            else if (alerts is not null && invoiceId is int soldInvoice) await AlertAsync(link, soldInvoice, paid, v.Channel, ct);   // admin WhatsApp + email, clerk and customer email
            else await notifier.NotifyAsync($"{company.LegalName}: payment received", summary, ct);
            await db.PaymentLinks.Where(l => l.Reference == reference).ExecuteUpdateAsync(u => u.SetProperty(l => l.NotifiedAt, clock.UtcNow), ct);
        }
        catch (Exception) { /* the payment is recorded; a failed message is retried by nobody, so it stays visible as NotifiedAt = null */ }
        return new SettleResult(true, false, summary, invoiceId, custId, custPhone, link.Provider, reference, null, false, paid, invoiceNumber);
    }
}