using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Sales;

/// <param name="StockReturned">Units put back on the shelf, per product.</param>
/// <param name="RefundDue">Money the customer paid in cash/transfer/card/online that must be handed back (recorded as refunded).</param>
/// <param name="RefundOnline">The part of <paramref name="RefundDue"/> paid through Paystack/AlatPay — refund it from that processor's dashboard.</param>
/// <param name="CreditRestored">Account credit that paid for this sale, put back on the customer's account.</param>
/// <param name="OwedCleared">What the customer still owed on this sale, taken off their balance.</param>
public sealed record InvoiceDeleteResult(string InvoiceNumber, string Customer, IReadOnlyList<StockChange> StockReturned, decimal RefundDue,
    IReadOnlyList<(string Method, decimal Amount)> RefundOnline, decimal CreditRestored, decimal OwedCleared);

/// <summary>
/// Deletes a sale as if it never happened, so the books always match the stock. In ONE transaction:
/// <list type="bullet">
/// <item>the stock it still holds goes back into the batches it came from (serial-numbered units back on the shelf);</item>
/// <item>money received for it is reversed: cash/transfer/card/online payments are recorded as refunded (so they leave income), account credit
/// that paid for it goes back on the customer's account, and what was still owed comes off their balance — with matching ledger entries;</item>
/// <item>its payments, waybills and line items are removed with it; unused payment links are cancelled; a quotation it came from is reopened;
/// accrued rebate from it is withdrawn;</item>
/// <item>a full copy is kept in the activity log with who deleted it and why.</item>
/// </list>
/// A sale whose rebate has already been redeemed can't be deleted (that value has left the business). Already-voided sales can be deleted too:
/// their stock and balance were reversed by the void, so only the money still recorded as paid on them is refunded.
/// </summary>
public sealed class InvoiceDeleteService(IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock)
{
    /// <summary>Payment methods that came through an online processor (the refund has to be made from its dashboard).</summary>
    /// (Online payments are recorded under the processor's display name — see PaymentLinkService.SettleAsync.)
    private static readonly string[] Online = [PaymentProviders.DisplayName(PaymentProviders.Paystack), PaymentProviders.DisplayName(PaymentProviders.AlatPay)];

    /// <summary>What deleting would do, without changing anything — shown to the admin before they confirm.</summary>
    public async Task<InvoiceDeleteResult> PreviewAsync(int invoiceId, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().Include(x => x.Items).Include(x => x.Payments).SingleOrDefaultAsync(x => x.Id == invoiceId, ct) ?? throw new NotFoundException("Sale");
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == i.CustomerId).Select(c => c.Name).SingleAsync(ct);
        var held = await stock.HeldByInvoiceAsync(i.Id, ct);
        return await ResultAsync(i, customer, held, ct);
    }

    public Task<InvoiceDeleteResult> DeleteAsync(int invoiceId, string reason, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("Say why this sale is being deleted.");
            // Lock order as elsewhere for this sale: customer, then invoice (batches are locked one by one as stock goes back).
            var peek = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId, inner) ?? throw new NotFoundException("Sale");
            var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {peek.CustomerId} FOR UPDATE").SingleAsync(inner);
            var invoice = await db.Invoices.FromSqlInterpolated($"SELECT * FROM invoices WHERE Id = {invoiceId} FOR UPDATE")
                .Include(x => x.Items).Include(x => x.Payments).SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Sale");

            var rebates = await db.RebateEntries.Where(r => r.InvoiceId == invoice.Id).ToListAsync(inner);
            if (rebates.Any(r => r.Status != "Accrued"))
                throw new BusinessRuleException("The rebate earned on this sale has already been redeemed, so it can't be deleted. Edit the sale instead.");

            var held = await stock.HeldByInvoiceAsync(invoice.Id, inner);
            var result = await ResultAsync(invoice, customer.Name, held, inner);
            var note = $"Deleted {invoice.InvoiceNumber}";

            // 1. Stock back, into the very batches it left (desktop-era sales with no batch go to the shared RETURNED batch).
            foreach (var h in held.OrderBy(h => h.BatchId ?? int.MaxValue).ThenBy(h => h.ProductId))
                await stock.ReturnHeldAsync(h, invoice.Id, MovementReferences.InvoiceDelete, user.Id, note, inner);
            foreach (var s in await db.ProductSerials.Where(s => s.InvoiceId == invoice.Id && s.Status == SerialStatuses.Sold).ToListAsync(inner))
            { s.Status = SerialStatuses.InStock; s.InvoiceId = null; s.SoldAt = null; }

            // 2. Money. The customer ledger reverses what the sale added (Credit), and the cash handed back is a refund paid out (Debit).
            //    A voided sale already reversed what was owed, so only what was paid on it is left to reverse.
            var voided = invoice.Status == PaymentStatuses.Voided;
            var reverse = voided ? invoice.AmountPaid : invoice.TotalAmount;
            customer.Balance -= reverse - result.RefundDue;
            if (reverse > 0) db.Ledger.Add(Entry(customer, LedgerEntryTypes.Credit, reverse, "DELETE " + invoice.InvoiceNumber));
            if (result.RefundDue > 0) db.Ledger.Add(Entry(customer, LedgerEntryTypes.Debit, result.RefundDue, "REFUND " + invoice.InvoiceNumber));

            // 3. Everything that pointed at the sale.
            db.RebateEntries.RemoveRange(rebates);
            db.Waybills.RemoveRange(await db.Waybills.Where(w => w.InvoiceId == invoice.Id).ToListAsync(inner));
            foreach (var q in await db.Quotations.Where(q => q.ConvertedInvoiceId == invoice.Id).ToListAsync(inner))
            { q.ConvertedInvoiceId = null; q.Status = QuotationStatuses.Open; }
            foreach (var l in await db.PaymentLinks.Where(l => l.DocType == Payments.PaymentDocTypes.Invoice && l.DocId == invoice.Id && l.Status == PaymentLinkStatuses.Pending).ToListAsync(inner))
                l.Status = PaymentLinkStatuses.Cancelled;

            // 4. The record itself — a full copy stays in the activity log.
            var snapshot = JsonSerializer.Serialize(new
            {
                invoice.InvoiceNumber, Customer = customer.Name, invoice.InvoiceDate, invoice.TotalAmount, invoice.AmountPaid, invoice.Status, invoice.PaymentMethod,
                Lines = invoice.Items.Select(x => new { x.ProductId, x.Quantity, x.UnitPrice, x.LineTotal }),
                Payments = invoice.Payments.Select(p => new { p.PaymentDate, p.Amount, p.Method }),
                result.RefundDue, result.CreditRestored, result.OwedCleared, Reason = reason.Trim(),
            });
            db.Payments.RemoveRange(invoice.Payments);
            db.InvoiceItems.RemoveRange(invoice.Items);
            db.Invoices.Remove(invoice);
            var summary = $"{invoice.InvoiceNumber} ({customer.Name}, ₦{invoice.TotalAmount:N2}) deleted: {reason.Trim()}."
                          + (result.RefundDue > 0 ? $" Refund ₦{result.RefundDue:N2}." : "") + (result.CreditRestored > 0 ? $" ₦{result.CreditRestored:N2} back to credit." : "");
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "INVOICE_DELETED", Entity = "Invoice", EntityId = invoiceId.ToString(), At = clock.UtcNow,
                Detail = (summary + " Record: " + snapshot) is var d && d.Length > 60000 ? d[..60000] : summary + " Record: " + snapshot });
            await db.SaveChangesAsync(inner);
            return result;
        }, ct);

    private async Task<InvoiceDeleteResult> ResultAsync(Invoice i, string customer, IReadOnlyList<StockService.HeldStock> held, CancellationToken ct)
    {
        // Account-credit payments net out (an edit can move money off this sale as a negative credit entry); only what the sale still holds counts.
        var creditNet = i.Payments.Where(p => p.Method == InvoiceEditService.AccountCredit).Sum(p => p.Amount);
        var creditRestored = Math.Clamp(creditNet, 0m, i.AmountPaid);
        var refund = i.AmountPaid - creditRestored;
        var online = i.Payments.Where(p => Online.Contains(p.Method)).GroupBy(p => p.Method)
            .Select(g => (Method: g.Key, Amount: Math.Min(g.Sum(p => p.Amount), refund))).Where(x => x.Amount > 0).ToList();
        var ids = held.Select(h => h.ProductId).Distinct().ToList();
        var names = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var back = held.GroupBy(h => h.ProductId).Select(g => new StockChange(g.Key, names.GetValueOrDefault(g.Key, "?"), g.Sum(h => h.Quantity))).ToList();
        var owed = i.Status == PaymentStatuses.Voided ? 0m : Math.Max(0m, i.TotalAmount - i.AmountPaid);
        return new InvoiceDeleteResult(i.InvoiceNumber, customer, back, refund, online, creditRestored, owed);
    }

    private LedgerEntry Entry(Customer c, string type, decimal amount, string reference) => new()
    {
        EntryDate = clock.BusinessToday, AccountType = LedgerAccountTypes.Customer, AccountName = c.Name, CustomerId = c.Id,
        EntryType = type, Amount = amount, Reference = reference,
    };
}
