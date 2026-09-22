using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Sales;

/// <summary>
/// Saving a sale (rules S1–S12). Invoice, lines, stock, stock history, customer balance, ledger, payments,
/// rebate and price-override log are written in ONE transaction; any failure rolls all of it back.
/// Concurrency: batches, the customer row and the customer's open invoices are locked (FOR UPDATE)
/// before anything is priced or written, in a fixed order.
/// </summary>
public sealed class SalesService(
    IBusinessDbContext db, TransactionRunner tx, StockService stock, IClock clock,
    ICompanyContext company, IValidator<SaleRequest> validator)
{
    private const string IdempotencyScope = "sale";

    public async Task<SaleResult> SaveAsync(SaleRequest req, CurrentUser user, string? idempotencyKey, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        return await tx.RunAsync(inner => SaveOnceAsync(req, user, idempotencyKey, inner), ct);
    }

    /// <summary>Saves a sale INSIDE a transaction the caller already opened (used to convert a quotation atomically).</summary>
    public async Task<SaleResult> SaveWithinAsync(SaleRequest req, CurrentUser user, string? idempotencyKey, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        return await SaveOnceAsync(req, user, idempotencyKey, ct);
    }

    private async Task<SaleResult> SaveOnceAsync(SaleRequest req, CurrentUser user, string? key, CancellationToken ct)
    {
        // Replay of an earlier successful request: return that invoice instead of selling twice.
        if (!string.IsNullOrEmpty(key))
        {
            var seen = await db.IdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.Scope == IdempotencyScope && r.Key == key, ct);
            if (seen?.ResultId is int existingId) return await ResultForExistingAsync(existingId, ct);
        }

        var lines = req.Lines;
        var saleDate = req.SaleDate ?? clock.BusinessToday;

        // 1. Lock every batch of every product on the order; refuse the whole sale if any line is short (S4).
        var batches = await stock.LockBatchesAsync(lines.Select(l => l.ProductId), ct);
        var products = await db.Products.Where(p => lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        foreach (var l in lines)
        {
            if (!products.TryGetValue(l.ProductId, out var p) || !p.IsActive)
                throw new BusinessRuleException("One of the products on this sale no longer exists or is inactive.");
        }
        var shortfalls = new List<Shortfall>();
        foreach (var g in lines.GroupBy(l => l.ProductId))
        {
            var wanted = g.Sum(l => l.Quantity);
            var have = batches[g.Key].Sum(b => b.QuantityOnHand);
            if (wanted > have)
            {
                var p = products[g.Key];
                var advice = await stock.AdviseAsync(p.Id, have, p.ReorderLevel, ct);
                shortfalls.Add(new Shortfall(p.Id, p.Name, wanted, have, advice.Summary));
            }
        }
        if (shortfalls.Count > 0) throw new InsufficientStockException(shortfalls);

        // 2. Lock the customer and read what they already owe (S6).
        var customer = await db.Customers
            .FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {req.CustomerId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Customer");
        if (!await db.Warehouses.AnyAsync(w => w.Id == req.WarehouseId, ct)) throw new NotFoundException("Warehouse");

        var previous = customer.Balance;
        var totals = DocumentCalculator.Sale(lines.Select(l => new SaleLineInput(l.ProductId, l.Quantity, l.UnitPrice)), req.DiscountPct, req.VatRate);
        var split = PaymentWaterfall.ForNewDocument(previous, totals.Total, req.PaidNow);

        var number = await DocumentNumbers.NextAsync(company, clock, (n, c) => db.Invoices.AnyAsync(i => i.InvoiceNumber == n, c), ct);
        var invoice = new Invoice
        {
            InvoiceNumber = number, CustomerId = customer.Id, InvoiceDate = saleDate,
            Subtotal = totals.Subtotal, DiscountPct = req.DiscountPct, DiscountAmount = totals.DiscountAmount,
            VatRate = req.VatRate, VatAmount = totals.VatAmount, TotalAmount = totals.Total,
            AmountPaid = split.AppliedToNew, Status = split.Status, PaymentMethod = req.PaymentMethod,
            DueDate = split.Outstanding > 0 ? req.DueDate : null, PriceTier = req.PriceTier, WarehouseId = req.WarehouseId,
            CreatedByUserId = user.Id, CreatedAt = clock.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);   // need the invoice id for movement references

        // 3. Lines, price-override log, stock deduction (S5, S10, S11).
        foreach (var l in lines)
        {
            var p = products[l.ProductId];
            db.InvoiceItems.Add(new InvoiceItem
            {
                InvoiceId = invoice.Id, ProductId = p.Id, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                UnitCost = p.CostPrice, LineTotal = l.Quantity * l.UnitPrice,
            });
            var standard = p.PriceFor(req.PriceTier);
            if (standard != l.UnitPrice)
            {
                db.PriceOverrides.Add(new PriceOverride
                {
                    DocType = "Invoice", DocNumber = number, ProductName = p.Name, StandardPrice = standard,
                    OverridePrice = l.UnitPrice, ChangedByName = user.FullName, ChangedAt = clock.UtcNow,
                });
            }
            stock.Deduct(batches[p.Id], p.Id, l.Quantity, req.WarehouseId, MovementReferences.Invoice, invoice.Id, user.Id);
            await ConsumeSerialsAsync(p, l, invoice, ct);
        }

        // 4. Overflow pays older invoices, oldest first (S6).
        var appliedToOld = 0m;
        if (split.Overflow > 0)
        {
            var open = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE CustomerId = {customer.Id} AND Status <> 'Paid' AND Status <> 'Voided' AND Id <> {invoice.Id} ORDER BY InvoiceDate, Id FOR UPDATE")
                .ToListAsync(ct);
            var (applications, applied) = PaymentWaterfall.Spread(open.Select(i => new OpenDocument(i.Id, i.TotalAmount, i.AmountPaid)), split.Overflow);
            foreach (var a in applications)
            {
                var old = open.First(i => i.Id == a.DocumentId);
                old.AmountPaid += a.Amount;
                old.Status = old.AmountPaid >= old.TotalAmount ? PaymentStatuses.Paid : PaymentStatuses.Partial;
                db.Payments.Add(new Payment { InvoiceId = old.Id, PaymentDate = clock.UtcNow, Amount = a.Amount, Method = req.PaymentMethod, ReceivedByUserId = user.Id });
            }
            appliedToOld = applied;
        }

        // 5. Customer balance, ledger, payment, rebate (S8, S9).
        customer.Balance = customer.Balance - split.PaidNow + totals.Total;
        if (appliedToOld > 0)
            db.Ledger.Add(Ledger(saleDate, customer, LedgerEntryTypes.Credit, appliedToOld, number));
        if (split.Outstanding > 0)
            db.Ledger.Add(Ledger(saleDate, customer, LedgerEntryTypes.Debit, split.Outstanding, number));
        if (split.AppliedToNew > 0)
            db.Payments.Add(new Payment { InvoiceId = invoice.Id, PaymentDate = clock.UtcNow, Amount = split.AppliedToNew, Method = req.PaymentMethod, ReceivedByUserId = user.Id });

        var rebate = RebateCalculator.Accrue(customer.CustomerType, totals.Total, totals.VatAmount, customer.RebateRatePct);
        if (rebate > 0)
        {
            db.RebateEntries.Add(new RebateEntry
            {
                CustomerId = customer.Id, InvoiceId = invoice.Id, EntryDate = saleDate, Amount = rebate, Status = "Accrued",
                Note = $"{customer.RebateRatePct}% of ₦{totals.Total - totals.VatAmount:N2} — {number}",
            });
        }

        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "SALE_CREATED", Entity = "Invoice", EntityId = invoice.Id.ToString(), At = clock.UtcNow, Detail = number });
        if (!string.IsNullOrEmpty(key))
            db.IdempotencyRecords.Add(new IdempotencyRecord { Scope = IdempotencyScope, Key = key, ResultId = invoice.Id, CreatedAt = clock.UtcNow });

        await db.SaveChangesAsync(ct);

        return new SaleResult(invoice.Id, number, totals.Subtotal, totals.DiscountAmount, totals.VatAmount, totals.Total,
            split.Outstanding, split.Status, previous, appliedToOld, previous - appliedToOld + split.Outstanding);
    }

    /// <summary>Marks the named serial units Sold against this invoice, or refuses the whole sale (rule T7).</summary>
    private async Task ConsumeSerialsAsync(Product p, SaleLineDto line, Invoice invoice, CancellationToken ct)
    {
        var wanted = (line.Serials ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (wanted.Count == 0) return;
        if (!p.TracksSerial) throw new BusinessRuleException($"{p.Name} does not track serial numbers.");
        if (wanted.Count != line.Quantity) throw new BusinessRuleException($"{p.Name}: enter exactly {line.Quantity} serial number(s), one per unit.");
        if (wanted.Distinct(StringComparer.OrdinalIgnoreCase).Count() != wanted.Count) throw new BusinessRuleException($"{p.Name}: the same serial is listed twice.");
        foreach (var sn in wanted)
        {
            var unit = await db.ProductSerials
                .FromSqlInterpolated($"SELECT * FROM product_serials WHERE ProductId = {p.Id} AND SerialNumber = {sn} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (unit is null || unit.Status != SerialStatuses.InStock)
                throw new BusinessRuleException($"Serial {sn} is not in stock — it may already be sold, returned or mistyped.");
            unit.Status = SerialStatuses.Sold; unit.SoldAt = clock.UtcNow; unit.InvoiceId = invoice.Id;
        }
    }

    private LedgerEntry Ledger(DateOnly date, Customer c, string type, decimal amount, string reference) => new()
    {
        EntryDate = date, AccountType = LedgerAccountTypes.Customer, AccountName = c.Name, CustomerId = c.Id,
        EntryType = type, Amount = amount, Reference = reference,
    };

    private async Task<SaleResult> ResultForExistingAsync(int invoiceId, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId, ct);
        return new SaleResult(i.Id, i.InvoiceNumber, i.Subtotal, i.DiscountAmount, i.VatAmount, i.TotalAmount,
            i.TotalAmount - i.AmountPaid, i.Status, 0, 0, 0);
    }
}
