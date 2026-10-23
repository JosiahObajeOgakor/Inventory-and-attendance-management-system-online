using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Partners;

public sealed record PortalLine(int ProductId, int Quantity);
public sealed record PortalOrder(IReadOnlyList<PortalLine> Lines, string Pay, string? Note);
/// <param name="PayUrl">AlatPay's secure page when paying online; null when the wallet paid.</param>
public sealed record PortalOrderResult(string Number, decimal Total, string Status, string? PayUrl, decimal WalletBalance, string Message);
public sealed record PortalFundResult(string Reference, decimal Amount, string PayUrl);
public sealed record PortalWalletEntry(string Kind, decimal Amount, string Status, string Reference, string Method, DateTime At, string? Note);
public sealed record PortalMessage(long Id, bool FromPartner, string Text, string? By, DateTime At);
public sealed record PortalNotice(string Kind, string Text, DateTime At);

/// <summary>
/// What an approved trade partner does in their portal for one store: buy at their own prices (paid from the wallet at once, or online through AlatPay),
/// top up the wallet through AlatPay, see every wallet movement, get told when money arrives or an order is confirmed, and message the business.
/// Every naira is checked on the server: prices come from the price list at the partner's tier, never from the browser.
/// </summary>
public sealed class PartnerPortalService(IBusinessDbContext db, IClock clock, ICompanyContext company, TransactionRunner tx, QuotationService quotations,
    CustomerPaymentService payments, PaymentLinkService pay, StockAvailabilityService stock, CompanyProfileService profile, IStaffAlerts alerts)
{
    public const decimal MinFunding = 1000m, MaxFunding = 50_000_000m;
    private static readonly CurrentUser Portal = new(0, "Partner portal", "SYSTEM");

    /// <summary>The approved partner behind this email in this store; refuses anyone else.</summary>
    private async Task<TradePartner> ApprovedAsync(string email, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email, ct) ?? throw new ForbiddenActionException("You don't have a trade account with this store.");
        if (p.Status != PartnerStatuses.Approved || p.CustomerId is null || !PriceTiers.IsValid(p.ApprovedTier))
            throw new ForbiddenActionException(p.Status == PartnerStatuses.Suspended ? "Your trade account is on hold. Please call us." : "Your trade account isn't approved yet.");
        return p;
    }

    public async Task<decimal> BalanceAsync(int partnerId, CancellationToken ct) =>
        await db.WalletEntries.Where(w => w.TradePartnerId == partnerId && w.Status == WalletEntryStatuses.Confirmed).SumAsync(w => (decimal?)w.Amount, ct) ?? 0;

    // ------------------------------------------------------------------ buying

    public async Task<PortalOrderResult> OrderAsync(string email, PortalOrder order, string returnUrl, CancellationToken ct)
    {
        var p = await ApprovedAsync(email, ct);
        if (order.Pay is not ("wallet" or "online")) throw new BusinessRuleException("Choose how to pay: wallet or online.");
        var lines = order.Lines.Where(l => l.Quantity > 0).GroupBy(l => l.ProductId).Select(g => new PortalLine(g.Key, g.Sum(l => l.Quantity))).ToList();
        if (lines.Count == 0) throw new BusinessRuleException("Add at least one product.");
        if (lines.Count > 60 || lines.Any(l => l.Quantity > 10_000)) throw new BusinessRuleException("That order is too large for the portal. Please message us.");

        // prices and stock from the records, at this partner's tier
        var cat = (await stock.CatalogueAsync(null, p.ApprovedTier!, ct)).ToDictionary(c => c.Id);
        foreach (var l in lines)
        {
            if (!cat.TryGetValue(l.ProductId, out var c)) throw new BusinessRuleException("A product in your basket is no longer available.");
            if (c.Available < l.Quantity) throw new BusinessRuleException($"Only {c.Available} {c.Unit.ToLowerInvariant()} of {c.Name} available right now.");
        }
        var vat = (await profile.GetAsync(ct)).DefaultVatRate;
        var quote = await quotations.CreateAsync(new QuoteRequest
        {
            CustomerId = p.CustomerId!.Value, PriceTier = p.ApprovedTier!, DiscountPct = 0, VatRate = vat,
            DeliveryAddress = string.IsNullOrWhiteSpace(order.Note) ? null : order.Note.Trim()[..Math.Min(250, order.Note.Trim().Length)],
            Lines = lines.Select(l => new SaleLineDto { ProductId = l.ProductId, Quantity = l.Quantity, UnitPrice = cat[l.ProductId].Price }).ToList(),
        }, Portal, ct);

        if (order.Pay == "online")
        {
            var link = await pay.GetOrCreateAsync(PaymentDocTypes.Quotation, quote.Id, quote.Number, quote.Total, p.Email, p.BusinessName, PaymentProviders.AlatPay, ct, returnUrl)
                ?? throw new BusinessRuleException("Online payment isn't available right now. Pay from your wallet or message us.");
            return new PortalOrderResult(quote.Number, quote.Total, "Awaiting payment", link.Url, await BalanceAsync(p.Id, ct), "Complete the payment on AlatPay's secure page. You'll be notified the moment it's confirmed.");
        }

        // paid from the wallet: one transaction, the partner's row locked so two clicks can't spend the same money twice
        SaleResult sale;
        decimal left;
        try
        {
            (sale, left) = await tx.RunAsync(async inner =>
            {
                await db.TradePartners.FromSqlInterpolated($"SELECT * FROM trade_partners WHERE Id = {p.Id} FOR UPDATE").SingleAsync(inner);
                var balance = await BalanceAsync(p.Id, inner);
                if (balance < quote.Total) throw new BusinessRuleException($"Your wallet has ₦{balance:N2}; this order is ₦{quote.Total:N2}. Top up your wallet or pay online.");
                var warehouseId = await WarehouseChooser.ForOrderAsync(db, company, clock, lines.Select(l => (l.ProductId, l.Quantity)).ToList(), inner);
                var s = await quotations.ConvertWithinAsync(quote.Id, new ConvertQuoteRequest { WarehouseId = warehouseId, PaymentMethod = "Bank Transfer", PaidNow = 0 }, Portal, inner);
                await payments.ApplyToInvoiceWithinAsync(s.InvoiceId, s.Total, "Wallet", Portal, inner, null, p.BusinessName);
                db.WalletEntries.Add(new WalletEntry
                {
                    TradePartnerId = p.Id, Kind = WalletEntryKinds.Purchase, Amount = -s.Total, Status = WalletEntryStatuses.Confirmed, Reference = $"INV-{s.InvoiceId}",
                    Method = "wallet", InvoiceId = s.InvoiceId, Note = s.InvoiceNumber, CreatedAt = clock.UtcNow, ConfirmedAt = clock.UtcNow, ConfirmedBy = "Partner portal",
                });
                await db.SaveChangesAsync(inner);
                return (s, balance - s.Total);
            }, ct);
        }
        catch (BusinessRuleException)
        {
            try { await quotations.DeleteAsync(quote.Id, Portal, ct); } catch (BusinessRuleException) { /* nothing to tidy */ }
            throw;
        }

        await alerts.TellStaffAsync(new StaffAlert($"{company.LegalName}: partner order {sale.InvoiceNumber} paid from wallet",
            $"🛒 PARTNER ORDER — {company.LegalName}\n{p.BusinessName} ({p.ApprovedTier}) bought {lines.Sum(l => l.Quantity)} item(s), ₦{sale.Total:N2}, paid from their wallet.\nInvoice {sale.InvoiceNumber}. Wallet left: ₦{left:N2}."
            + (string.IsNullOrWhiteSpace(order.Note) ? "" : $"\nDelivery note: {order.Note}")), ct);
        await alerts.TellContactAsync(new ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: order {sale.InvoiceNumber} confirmed",
            $"Thank you, {First(p.ContactName)}. Your order {sale.InvoiceNumber} for ₦{sale.Total:N2} is confirmed and paid from your wallet.\n\nWallet balance now: ₦{left:N2}. We'll be in touch about delivery."), ct);
        return new PortalOrderResult(sale.InvoiceNumber, sale.Total, "Paid", null, left, $"Order {sale.InvoiceNumber} is confirmed and paid from your wallet.");
    }

    // ------------------------------------------------------------------ wallet

    /// <summary>A top-up on AlatPay's secure page. The wallet is credited only once AlatPay confirms the money (exactly once, by its reference).</summary>
    public async Task<PortalFundResult> FundAsync(string email, decimal amount, string returnUrl, CancellationToken ct)
    {
        var p = await ApprovedAsync(email, ct);
        amount = Math.Round(amount, 2);
        if (amount < MinFunding || amount > MaxFunding) throw new BusinessRuleException($"Top up between ₦{MinFunding:N0} and ₦{MaxFunding:N0}.");
        var link = await pay.GetOrCreateAsync(PaymentDocTypes.Wallet, p.Id, $"WALLET-{p.Id}", amount, p.Email, p.BusinessName, PaymentProviders.AlatPay, ct, returnUrl)
            ?? throw new BusinessRuleException("Online payment isn't available right now. Please message us.");
        if (!await db.WalletEntries.AnyAsync(w => w.Reference == link.Reference, ct))
        {
            db.WalletEntries.Add(new WalletEntry { TradePartnerId = p.Id, Kind = WalletEntryKinds.Funding, Amount = amount, Status = WalletEntryStatuses.Pending, Reference = link.Reference,
                Method = "alatpay", CreatedAt = clock.UtcNow, Note = "Top-up" });
            await db.SaveChangesAsync(ct);
        }
        return new PortalFundResult(link.Reference, amount, link.Url);
    }

    /// <summary>Back from AlatPay: asks AlatPay straight away instead of waiting for its callback, so the partner sees the result at once.</summary>
    public async Task<string> ConfirmAsync(string email, CancellationToken ct)
    {
        var p = await ApprovedAsync(email, ct);
        var since = clock.UtcNow.AddDays(-2);
        var pending = await db.PaymentLinks.AsNoTracking()
            .Where(l => l.Status == PaymentLinkStatuses.Pending && l.CreatedAt > since && ((l.DocType == PaymentDocTypes.Wallet && l.DocId == p.Id)
                || (l.DocType == PaymentDocTypes.Quotation && db.Quotations.Any(q => q.Id == l.DocId && q.CustomerId == p.CustomerId))))
            .Select(l => l.Reference).ToListAsync(ct);
        var done = 0;
        foreach (var r in pending) if ((await pay.SettleAsync(r, ct)).Applied) done++;
        return done == 0 ? "We haven't seen the payment yet. It usually shows within a minute; you'll be notified as soon as it does." : "Payment confirmed. Thank you!";
    }

    public async Task<List<PortalWalletEntry>> WalletAsync(string email, CancellationToken ct)
    {
        var p = await ApprovedAsync(email, ct);
        return await db.WalletEntries.AsNoTracking().Where(w => w.TradePartnerId == p.Id && w.Status != WalletEntryStatuses.Failed).OrderByDescending(w => w.Id).Take(50)
            .Select(w => new PortalWalletEntry(w.Kind, w.Amount, w.Status, w.Reference, w.Method, w.ConfirmedAt ?? w.CreatedAt, w.Note)).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ messages and notices

    public async Task<List<PortalMessage>> MessagesAsync(string email, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email, ct) ?? throw new ForbiddenActionException("You don't have a trade account with this store.");
        await db.PartnerMessages.Where(m => m.TradePartnerId == p.Id && !m.FromPartner && m.ReadAt == null).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadAt, clock.UtcNow), ct);
        return await db.PartnerMessages.AsNoTracking().Where(m => m.TradePartnerId == p.Id).OrderByDescending(m => m.Id).Take(100).OrderBy(m => m.Id)
            .Select(m => new PortalMessage(m.Id, m.FromPartner, m.Text, m.By, m.At)).ToListAsync(ct);
    }

    public async Task SendAsync(string email, string text, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email, ct) ?? throw new ForbiddenActionException("You don't have a trade account with this store.");
        text = (text ?? "").Trim();
        if (text.Length is < 1 or > 2000) throw new BusinessRuleException("Write a message (up to 2,000 characters).");
        var recent = await db.PartnerMessages.CountAsync(m => m.TradePartnerId == p.Id && m.FromPartner && m.At > clock.UtcNow.AddMinutes(-10), ct);
        if (recent >= 20) throw new BusinessRuleException("You've sent a lot of messages just now. We'll reply as soon as we can.");
        db.PartnerMessages.Add(new PartnerMessage { TradePartnerId = p.Id, FromPartner = true, Text = text, At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        await alerts.TellStaffAsync(new StaffAlert($"{company.LegalName}: message from {p.BusinessName}",
            $"💬 PARTNER MESSAGE — {p.BusinessName} ({TradePartnerService.Pretty(p.Phone)})\n\"{(text.Length > 500 ? text[..500] + "…" : text)}\"\nReply in Inventory → Trade partners.", AdminOnly: true), ct);
    }

    /// <summary>Staff reply (Inventory → Trade partners); the partner is told by email and WhatsApp.</summary>
    public async Task ReplyAsync(int partnerId, string text, CurrentUser user, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Id == partnerId, ct) ?? throw new NotFoundException("Trade partner");
        text = (text ?? "").Trim();
        if (text.Length is < 1 or > 2000) throw new BusinessRuleException("Write a reply (up to 2,000 characters).");
        db.PartnerMessages.Add(new PartnerMessage { TradePartnerId = p.Id, FromPartner = false, Text = text, By = user.FullName, At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        await alerts.TellContactAsync(new ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: new message",
            $"Hello {First(p.ContactName)}, {user.FullName} replied:\n\n\"{text}\"\n\nOpen your partner account to answer."), ct);
    }

    public async Task<List<PortalMessage>> ThreadAsync(int partnerId, CancellationToken ct) =>
        await db.PartnerMessages.AsNoTracking().Where(m => m.TradePartnerId == partnerId).OrderBy(m => m.Id).Take(500)
            .Select(m => new PortalMessage(m.Id, m.FromPartner, m.Text, m.By, m.At)).ToListAsync(ct);

    /// <summary>What happened lately: money in, orders confirmed, replies from the business.</summary>
    public async Task<List<PortalNotice>> NoticesAsync(string email, CancellationToken ct)
    {
        var p = await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email, ct);
        if (p is null) return [];
        var since = clock.UtcNow.AddDays(-30);
        var notices = new List<PortalNotice>();
        foreach (var w in await db.WalletEntries.AsNoTracking().Where(w => w.TradePartnerId == p.Id && w.Status == WalletEntryStatuses.Confirmed && w.ConfirmedAt > since).OrderByDescending(w => w.Id).Take(20).ToListAsync(ct))
            notices.Add(new PortalNotice(w.Kind == WalletEntryKinds.Funding ? "payment" : "order",
                w.Kind == WalletEntryKinds.Funding ? $"₦{w.Amount:N2} added to your wallet" : $"Order {w.Note} paid from wallet (₦{-w.Amount:N2})", w.ConfirmedAt!.Value));
        if (p.CustomerId is int cid)
            foreach (var l in await (from l in db.PaymentLinks.AsNoTracking() join q in db.Quotations.AsNoTracking() on l.DocId equals q.Id
                                     where l.DocType == PaymentDocTypes.Quotation && q.CustomerId == cid && l.Status == PaymentLinkStatuses.Paid && l.PaidAt > since
                                     orderby l.PaidAt descending select new { l.DocNumber, l.PaidAmount, l.PaidAt }).Take(20).ToListAsync(ct))
                notices.Add(new PortalNotice("order", $"Order {l.DocNumber} paid online (₦{l.PaidAmount:N2}) and confirmed", l.PaidAt!.Value));
        foreach (var m in await db.PartnerMessages.AsNoTracking().Where(m => m.TradePartnerId == p.Id && !m.FromPartner && m.At > since).OrderByDescending(m => m.Id).Take(10).ToListAsync(ct))
            notices.Add(new PortalNotice("message", $"{m.By} replied: {(m.Text.Length > 80 ? m.Text[..80] + "…" : m.Text)}", m.At));
        return notices.OrderByDescending(n => n.At).Take(30).ToList();
    }

    private static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}
