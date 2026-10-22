using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Sales;

/// <summary>The new contents of an existing sale. The customer and the sale's number never change.</summary>
public sealed class InvoiceEditRequest
{
    public decimal DiscountPct { get; set; }
    /// <summary>A fixed naira discount; when set (&gt; 0) it wins over <see cref="DiscountPct"/>.</summary>
    public decimal? DiscountAmount { get; set; }
    public decimal VatRate { get; set; }
    public decimal DeliveryFee { get; set; }
    public DateOnly? DueDate { get; set; }
    public List<SaleLineDto> Lines { get; set; } = [];
}

public sealed record StockChange(int ProductId, string Product, int Quantity);

/// <param name="StockBack">Units put back on the shelf (removed or reduced lines).</param>
/// <param name="StockOut">Extra units taken off the shelf (added or increased lines).</param>
/// <param name="MovedToOtherSales">Money already paid that now exceeds the new total and was applied to this customer's other unpaid sales.</param>
/// <param name="CreditHeld">What was left after that, kept as account credit for the customer's next sale.</param>
public sealed record InvoiceEditResult(int InvoiceId, string InvoiceNumber, decimal OldTotal, decimal NewTotal, decimal Outstanding, string Status,
    IReadOnlyList<StockChange> StockBack, IReadOnlyList<StockChange> StockOut, decimal MovedToOtherSales, decimal CreditHeld);

public sealed class InvoiceEditRequestValidator : AbstractValidator<InvoiceEditRequest>
{
    public InvoiceEditRequestValidator()
    {
        RuleFor(x => x.DiscountPct).InclusiveBetween(0, 100);
        RuleFor(x => x.DiscountAmount).GreaterThanOrEqualTo(0).When(x => x.DiscountAmount.HasValue);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.DeliveryFee).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("A sale needs at least one item. To cancel it completely, void it instead.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).GreaterThan(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>
/// Changes the items on a sale that has already been saved (a customer swapped something after the receipt was printed). Only the DIFFERENCE moves:
/// units taken off the order go back into the very batches they came from, extra units come off the shelf first-expiry-first, and nothing is counted
/// twice. The total, the customer's balance, any rebate and the ledger follow; money already paid beyond the new total pays the customer's other
/// unpaid sales first and the rest is held as account credit. One transaction — a refused edit (e.g. not enough stock) changes nothing.
/// </summary>
public sealed class InvoiceEditService(IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock)
{
    public const string AccountCredit = "Account credit";

    public async Task<InvoiceEditResult> EditAsync(int invoiceId, InvoiceEditRequest req, CurrentUser user, CancellationToken ct = default)
    {
        await new InvoiceEditRequestValidator().ValidateAndThrowAsync(req, ct);
        return await tx.RunAsync(inner => EditOnceAsync(invoiceId, req, user, inner), ct);
    }

    private async Task<InvoiceEditResult> EditOnceAsync(int invoiceId, InvoiceEditRequest req, CurrentUser user, CancellationToken ct)
    {
        // Lock in the same order a new sale does (batches, then customer, then invoice) so an edit and a sale can't deadlock.
        var peek = await db.Invoices.AsNoTracking().Include(i => i.Items).SingleOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");
        var productIds = peek.Items.Select(i => i.ProductId).Concat(req.Lines.Select(l => l.ProductId)).Distinct().ToList();
        var batches = await stock.LockBatchesAsync(productIds, ct);
        var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {peek.CustomerId} FOR UPDATE").SingleAsync(ct);
        var invoice = await db.Invoices.FromSqlInterpolated($"SELECT * FROM invoices WHERE Id = {invoiceId} FOR UPDATE").Include(i => i.Items).SingleAsync(ct);
        if (invoice.Status == PaymentStatuses.Voided) throw new BusinessRuleException("A voided sale can't be edited.");

        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var before = invoice.Items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var after = req.Lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        foreach (var pid in after.Keys.Where(pid => !before.ContainsKey(pid)))
            if (!products.TryGetValue(pid, out var p) || !p.IsActive) throw new BusinessRuleException("One of the added products no longer exists or is inactive.");

        var deltas = productIds.Select(pid => (Pid: pid, Delta: after.GetValueOrDefault(pid) - before.GetValueOrDefault(pid))).Where(d => d.Delta != 0).ToList();
        var serial = deltas.Where(d => products[d.Pid].TracksSerial).Select(d => products[d.Pid].Name).ToList();
        if (serial.Count > 0)
            throw new BusinessRuleException($"{string.Join(", ", serial)} {(serial.Count == 1 ? "is" : "are")} tracked by serial number, so the quantity can't be edited here. Delete the sale and enter it again.");

        // Refuse up front if any added units aren't on the shelf — nothing has been touched yet.
        var shortfalls = new List<Shortfall>();
        foreach (var (pid, delta) in deltas.Where(d => d.Delta > 0))
        {
            var have = batches[pid].Sum(b => b.QuantityOnHand);
            if (delta > have)
            {
                var p = products[pid];
                var advice = await stock.AdviseAsync(pid, have, p.ReorderLevel, ct);
                shortfalls.Add(new Shortfall(pid, p.Name, delta, have, advice.Summary));
            }
        }
        if (shortfalls.Count > 0) throw new InsufficientStockException(shortfalls);

        // 1. Stock: only the difference.
        var note = $"Edit of {invoice.InvoiceNumber}";
        var back = new List<StockChange>(); var outs = new List<StockChange>();
        foreach (var (pid, delta) in deltas.Where(d => d.Delta < 0).OrderBy(d => d.Pid))
        {
            await stock.ReturnFromInvoiceAsync(invoice.Id, pid, -delta, MovementReferences.InvoiceEdit, user.Id, note, ct);
            back.Add(new StockChange(pid, products[pid].Name, -delta));
        }
        foreach (var (pid, delta) in deltas.Where(d => d.Delta > 0).OrderBy(d => d.Pid))
        {
            stock.Deduct(batches[pid], pid, delta, invoice.WarehouseId ?? 0, MovementReferences.InvoiceEdit, invoice.Id, user.Id, note);
            outs.Add(new StockChange(pid, products[pid].Name, delta));
        }

        // 2. Lines. A product already on the sale keeps the cost it was sold at; a new one takes today's cost.
        var oldCost = invoice.Items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.First().UnitCost);
        var oldPrice = invoice.Items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.First().UnitPrice);
        db.InvoiceItems.RemoveRange(invoice.Items);
        foreach (var l in req.Lines)
        {
            var p = products[l.ProductId];
            db.InvoiceItems.Add(new InvoiceItem
            {
                InvoiceId = invoice.Id, ProductId = p.Id, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                UnitCost = oldCost.TryGetValue(p.Id, out var c) ? c : p.CostPrice, LineTotal = l.Quantity * l.UnitPrice,
            });
            var standard = p.PriceFor(invoice.PriceTier);
            if (l.UnitPrice != standard && (!oldPrice.TryGetValue(p.Id, out var was) || was != l.UnitPrice))
                db.PriceOverrides.Add(new PriceOverride { DocType = "Invoice", DocNumber = invoice.InvoiceNumber, ProductName = p.Name, StandardPrice = standard, OverridePrice = l.UnitPrice, ChangedByName = user.FullName, ChangedAt = clock.UtcNow });
        }

        // 3. Totals (delivery goes on after VAT, as on a new sale).
        var totals = DocumentCalculator.Sale(req.Lines.Select(l => new SaleLineInput(l.ProductId, l.Quantity, l.UnitPrice)), req.DiscountPct, req.VatRate, req.DiscountAmount);
        var oldTotal = invoice.TotalAmount;
        var newTotal = totals.Total + req.DeliveryFee;
        invoice.Subtotal = totals.Subtotal; invoice.DiscountPct = req.DiscountAmount is > 0 ? DocumentCalculator.EffectivePct(totals.Subtotal, totals.DiscountAmount) : req.DiscountPct; invoice.DiscountAmount = totals.DiscountAmount;
        invoice.VatRate = req.VatRate; invoice.VatAmount = totals.VatAmount; invoice.DeliveryFee = req.DeliveryFee; invoice.TotalAmount = newTotal;

        // 4. Money. The customer now owes the difference (or is owed it).
        var change = newTotal - oldTotal;
        customer.Balance += change;
        if (change != 0)
            db.Ledger.Add(new LedgerEntry
            {
                EntryDate = clock.BusinessToday, AccountType = LedgerAccountTypes.Customer, AccountName = customer.Name, CustomerId = customer.Id,
                EntryType = change > 0 ? LedgerEntryTypes.Debit : LedgerEntryTypes.Credit, Amount = Math.Abs(change), Reference = "EDIT " + invoice.InvoiceNumber,
            });

        decimal moved = 0, credit = 0;
        if (invoice.AmountPaid > newTotal)
        {
            // Paid more than the sale now costs: the extra settles the customer's other unpaid sales, oldest first, and the rest waits as credit.
            var excess = invoice.AmountPaid - newTotal;
            invoice.AmountPaid = newTotal;
            var open = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE CustomerId = {customer.Id} AND Status <> 'Paid' AND Status <> 'Voided' AND Id <> {invoice.Id} ORDER BY InvoiceDate, Id FOR UPDATE")
                .ToListAsync(ct);
            var (apps, applied) = PaymentWaterfall.Spread(open.Select(i => new OpenDocument(i.Id, i.TotalAmount, i.AmountPaid)), excess);
            foreach (var a in apps)
            {
                var other = open.First(i => i.Id == a.DocumentId);
                other.AmountPaid += a.Amount;
                other.Status = PaymentStatuses.For(other.AmountPaid, other.TotalAmount);
                db.Payments.Add(new Payment { InvoiceId = other.Id, PaymentDate = clock.UtcNow, Amount = a.Amount, Method = AccountCredit, ReceivedByUserId = user.Id });
            }
            moved = applied;
            credit = excess - applied;
            // The money leaves this sale (a negative payment row), so each sale's payments still add up to what it shows as paid,
            // and the business's total money received is unchanged.
            db.Payments.Add(new Payment { InvoiceId = invoice.Id, PaymentDate = clock.UtcNow, Amount = -excess, Method = AccountCredit, ReceivedByUserId = user.Id });
        }
        invoice.Status = PaymentStatuses.For(invoice.AmountPaid, newTotal);
        invoice.DueDate = invoice.Status == PaymentStatuses.Paid ? null : req.DueDate ?? invoice.DueDate;

        // 5. Rebate follows the goods total (never the delivery fee), unless it has already been redeemed.
        var rebates = await db.RebateEntries.Where(r => r.InvoiceId == invoice.Id).ToListAsync(ct);
        if (rebates.All(r => r.Status == "Accrued"))
        {
            db.RebateEntries.RemoveRange(rebates);
            var units = req.Lines.Sum(l => l.Quantity);
            var rebate = RebateCalculator.Accrue(customer.CustomerType, units, customer.RebatePerUnit);
            if (rebate > 0)
                db.RebateEntries.Add(new RebateEntry
                {
                    CustomerId = customer.Id, InvoiceId = invoice.Id, EntryDate = invoice.InvoiceDate, Amount = rebate, Status = "Accrued",
                    Note = $"₦{customer.RebatePerUnit:N2} × {units:N0} unit(s) — {invoice.InvoiceNumber} (edited)",
                });
        }

        var summary = string.Join("; ", new[]
        {
            back.Count > 0 ? "Back to stock: " + string.Join(", ", back.Select(b => $"{b.Quantity} × {b.Product}")) : null,
            outs.Count > 0 ? "Taken from stock: " + string.Join(", ", outs.Select(o => $"{o.Quantity} × {o.Product}")) : null,
            $"total ₦{oldTotal:N2} → ₦{newTotal:N2}",
            moved > 0 ? $"₦{moved:N2} moved to other unpaid sales" : null,
            credit > 0 ? $"₦{credit:N2} held as account credit" : null,
        }.Where(s => s is not null));
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "INVOICE_EDITED", Entity = "Invoice", EntityId = invoice.Id.ToString(), At = clock.UtcNow, Detail = Cut($"{invoice.InvoiceNumber}: {summary}", 1000) });
        await db.SaveChangesAsync(ct);

        return new InvoiceEditResult(invoice.Id, invoice.InvoiceNumber, oldTotal, newTotal, newTotal - invoice.AmountPaid, invoice.Status, back, outs, moved, credit);
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
