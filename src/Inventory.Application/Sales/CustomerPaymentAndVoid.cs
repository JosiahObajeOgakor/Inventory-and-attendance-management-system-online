using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Sales;

/// <summary>C2: "Record payment" — spreads across the customer's open invoices oldest first.</summary>
public sealed class CustomerPaymentService(IBusinessDbContext db, TransactionRunner tx, IClock clock)
{
    public Task<decimal> RecordAsync(int customerId, decimal amount, string method, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            if (amount <= 0) throw new BusinessRuleException("Enter an amount greater than zero.");
            var customer = await db.Customers
                .FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {customerId} FOR UPDATE")
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Customer");
            if (customer.Balance <= 0) throw new BusinessRuleException("This customer has no outstanding balance.");
            if (amount > customer.Balance) throw new BusinessRuleException($"That is more than the customer owes (₦{customer.Balance:N2}).");

            var open = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE CustomerId = {customerId} AND Status <> 'Paid' AND Status <> 'Voided' ORDER BY InvoiceDate, Id FOR UPDATE")
                .ToListAsync(inner);
            var (apps, applied) = PaymentWaterfall.Spread(open.Select(i => new OpenDocument(i.Id, i.TotalAmount, i.AmountPaid)), amount);
            foreach (var a in apps)
            {
                var inv = open.First(i => i.Id == a.DocumentId);
                inv.AmountPaid += a.Amount;
                inv.Status = inv.AmountPaid >= inv.TotalAmount ? PaymentStatuses.Paid : PaymentStatuses.Partial;
                db.Payments.Add(new Payment { InvoiceId = inv.Id, PaymentDate = clock.UtcNow, Amount = a.Amount, Method = method, ReceivedByUserId = user.Id });
            }
            customer.Balance -= amount;
            db.Ledger.Add(new LedgerEntry
            {
                EntryDate = clock.BusinessToday, AccountType = LedgerAccountTypes.Customer, AccountName = customer.Name, CustomerId = customer.Id,
                EntryType = LedgerEntryTypes.Credit, Amount = amount, Reference = "Payment",
            });
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "CUSTOMER_PAYMENT", Entity = "Customer", EntityId = customerId.ToString(), At = clock.UtcNow, Detail = $"₦{amount:N2} via {method}" });
            await db.SaveChangesAsync(inner);
            return applied;
        }, ct);

    /// <summary>
    /// Money that arrived for ONE specific invoice (an online payment): applied to that invoice only, capped at what is still owed on it.
    /// Returns what was applied and what was left over (an overpayment). Runs inside the caller's transaction. Locks customer first, then invoice,
    /// the same order as <see cref="RecordAsync"/>, so the two can never deadlock.
    /// </summary>
    public async Task<(decimal Applied, decimal Leftover)> ApplyToInvoiceWithinAsync(int invoiceId, decimal amount, string method, CurrentUser user, CancellationToken ct)
    {
        var customerId = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => (int?)i.CustomerId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Invoice");
        var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {customerId} FOR UPDATE").SingleAsync(ct);
        var inv = await db.Invoices.FromSqlInterpolated($"SELECT * FROM invoices WHERE Id = {invoiceId} FOR UPDATE").SingleAsync(ct);
        var apply = inv.Status == PaymentStatuses.Voided ? 0m : Math.Min(inv.TotalAmount - inv.AmountPaid, amount);
        if (apply <= 0) return (0, amount);

        inv.AmountPaid += apply;
        inv.Status = inv.AmountPaid >= inv.TotalAmount ? PaymentStatuses.Paid : PaymentStatuses.Partial;
        db.Payments.Add(new Payment { InvoiceId = inv.Id, PaymentDate = clock.UtcNow, Amount = apply, Method = method, ReceivedByUserId = user.Id });
        customer.Balance -= apply;
        db.Ledger.Add(new LedgerEntry
        {
            EntryDate = clock.BusinessToday, AccountType = LedgerAccountTypes.Customer, AccountName = customer.Name, CustomerId = customer.Id,
            EntryType = LedgerEntryTypes.Credit, Amount = apply, Reference = $"{method} {inv.InvoiceNumber}",
        });
        await db.SaveChangesAsync(ct);
        return (apply, amount - apply);
    }
}

/// <summary>
/// Replaces the desktop "Delete invoice" (defect D1). The invoice is KEPT with status Voided, and everything it did is
/// reversed in one transaction: stock goes back (one IN movement per line, to the warehouse the units left), the customer's
/// balance is reduced by what was still owed, rebate accruals are cancelled, and ledger reversals are posted.
/// </summary>
public sealed class InvoiceVoidService(IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock)
{
    public Task VoidAsync(int invoiceId, string reason, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("A reason is required to void an invoice.");
            var invoice = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE Id = {invoiceId} FOR UPDATE")
                .Include(i => i.Items)
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Invoice");
            if (invoice.Status == PaymentStatuses.Voided) throw new BusinessRuleException("This invoice has already been voided.");

            var customer = await db.Customers
                .FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {invoice.CustomerId} FOR UPDATE")
                .SingleAsync(inner);

            // Stock back, into the very batch each unit left (their OUT movements say which), so expiry dates survive.
            // Sales recorded by the desktop app carry no batch id; those units go to one shared "RETURNED" batch per
            // warehouse rather than a new batch per voided invoice.
            var outs = await db.StockMovements
                .Where(m => m.ReferenceType == MovementReferences.Invoice && m.ReferenceId == invoice.Id && m.MovementType == MovementTypes.Out)
                .ToListAsync(inner);
            foreach (var m in outs.OrderBy(m => m.BatchId ?? int.MaxValue).ThenBy(m => m.ProductId))
            {
                var note = $"Void of {invoice.InvoiceNumber}";
                StockBatch? original = m.BatchId is int bid
                    ? await db.StockBatches.FromSqlInterpolated($"SELECT * FROM stock_batches WHERE Id = {bid} FOR UPDATE").SingleOrDefaultAsync(inner)
                    : null;
                if (original is not null)
                {
                    original.QuantityOnHand += m.Quantity;
                    stock.AddMovement(m.ProductId, m.WarehouseId, MovementTypes.In, m.Quantity, MovementReferences.InvoiceVoid, invoice.Id, user.Id, note, original.Id);
                }
                else
                {
                    await stock.ReceiveAsync(m.ProductId, m.WarehouseId, "RETURNED", m.Quantity, null, MovementReferences.InvoiceVoid, invoice.Id, user.Id, inner, note);
                }
            }

            // What the customer still owed on this invoice comes off their balance; what they already paid stays paid
            // (refund is a business decision outside the system and is recorded in the void reason).
            var owed = invoice.TotalAmount - invoice.AmountPaid;
            customer.Balance -= owed;
            if (owed > 0)
                db.Ledger.Add(new LedgerEntry
                {
                    EntryDate = clock.BusinessToday, AccountType = LedgerAccountTypes.Customer, AccountName = customer.Name, CustomerId = customer.Id,
                    EntryType = LedgerEntryTypes.Credit, Amount = owed, Reference = "VOID " + invoice.InvoiceNumber,
                });

            // Individually tracked units sold on this invoice go back on the shelf.
            var sold = await db.ProductSerials.Where(s => s.InvoiceId == invoice.Id && s.Status == SerialStatuses.Sold).ToListAsync(inner);
            foreach (var s in sold) { s.Status = SerialStatuses.InStock; s.InvoiceId = null; s.SoldAt = null; }

            var rebates = await db.RebateEntries.Where(r => r.InvoiceId == invoice.Id && r.Status == "Accrued").ToListAsync(inner);
            db.RebateEntries.RemoveRange(rebates);

            invoice.Status = PaymentStatuses.Voided;
            invoice.VoidedAt = clock.UtcNow;
            invoice.VoidedByUserId = user.Id;
            invoice.VoidReason = reason.Trim();
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "INVOICE_VOIDED", Entity = "Invoice", EntityId = invoice.Id.ToString(), At = clock.UtcNow, Detail = $"{invoice.InvoiceNumber}: {reason.Trim()}" });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);
}
