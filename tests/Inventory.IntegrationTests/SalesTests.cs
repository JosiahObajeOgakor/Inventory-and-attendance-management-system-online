using Inventory.Application.Common;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

[Collection("mysql")]
public class SalesTests(MySqlFixture mysql)
{
    [Fact]
    public async Task A_sale_writes_everything_and_deducts_stock()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);

        await using var db = NewContext(cs);
        var r = await SalesFor(db).SaveAsync(Sale(seed, qty: 2, paid: 10000), Clerk, null);

        Assert.Equal(23000m, r.Subtotal);
        Assert.Equal(1725m, r.VatAmount);          // 23000 * 7.5%
        Assert.Equal(24725m, r.Total);
        Assert.Equal("Partial", r.Status);
        Assert.Equal(14725m, r.Outstanding);

        await using var check = NewContext(cs);
        Assert.Equal(8, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        var mv = await check.StockMovements.SingleAsync();
        Assert.Equal(("OUT", 2, "Invoice"), (mv.MovementType, mv.Quantity, mv.ReferenceType));
        Assert.Equal(14725m, (await check.Customers.SingleAsync()).Balance);
        var ledger = await check.Ledger.SingleAsync();
        Assert.Equal(("Debit", 14725m), (ledger.EntryType, ledger.Amount));
        Assert.Equal(r.InvoiceNumber, ledger.Reference);
        Assert.Equal(10000m, (await check.Payments.SingleAsync()).Amount);
        Assert.Equal(230m, (await check.RebateEntries.SingleAsync()).Amount);   // 1% of net sales 23,000 (total − VAT)
        Assert.Single(await check.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Insufficient_stock_refuses_the_whole_sale_and_writes_nothing()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 3);

        await using var db = NewContext(cs);
        var ex = await Assert.ThrowsAsync<InsufficientStockException>(() => SalesFor(db).SaveAsync(Sale(seed, qty: 4), Clerk, null));
        Assert.Equal(1, ex.Shortfalls[0].ShortBy);

        await using var check = NewContext(cs);
        Assert.Equal(3, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        Assert.Empty(await check.Invoices.ToListAsync());
        Assert.Empty(await check.StockMovements.ToListAsync());
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task A_failure_part_way_rolls_everything_back()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 5);

        // Unknown warehouse is detected AFTER batches are locked and the customer read — nothing may leak.
        var req = Sale(seed, qty: 1);
        req.WarehouseId = 9999;
        await using var db = NewContext(cs);
        await Assert.ThrowsAsync<NotFoundException>(() => SalesFor(db).SaveAsync(req, Clerk, null));

        await using var check = NewContext(cs);
        Assert.Equal(5, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        Assert.Empty(await check.Invoices.ToListAsync());
        Assert.Empty(await check.Ledger.ToListAsync());
    }

    [Fact]
    public async Task Parallel_sales_of_the_last_units_never_oversell()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 3);

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = NewContext(cs);
            try { await SalesFor(db).SaveAsync(Sale(seed, qty: 1, paid: 100), Clerk, null); return true; }
            catch (InsufficientStockException) { return false; }
        }).ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(3, outcomes.Count(o => o));
        await using var check = NewContext(cs);
        Assert.Equal(0, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        Assert.Equal(3, await check.Invoices.CountAsync());
        Assert.Equal(3, await check.StockMovements.CountAsync());
        // Customer balance is the sum of what was actually sold minus paid — no lost updates.
        var expected = await check.Invoices.SumAsync(i => i.TotalAmount) - await check.Payments.SumAsync(p => p.Amount);
        Assert.Equal(expected, (await check.Customers.SingleAsync()).Balance);
        Assert.Equal(3, await check.Invoices.Select(i => i.InvoiceNumber).Distinct().CountAsync());
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_does_not_sell_twice()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);

        await using var a = NewContext(cs);
        var first = await SalesFor(a).SaveAsync(Sale(seed, 1), Clerk, "key-123");
        await using var b = NewContext(cs);
        var second = await SalesFor(b).SaveAsync(Sale(seed, 1), Clerk, "key-123");

        Assert.Equal(first.InvoiceId, second.InvoiceId);
        await using var check = NewContext(cs);
        Assert.Equal(9, await check.StockBatches.SumAsync(x => x.QuantityOnHand));
        Assert.Single(await check.Invoices.ToListAsync());
    }

    [Fact]
    public async Task Overflow_payment_clears_older_invoices_oldest_first()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using (var db = NewContext(cs))
            await SalesFor(db).SaveAsync(Sale(seed, 1, vat: 0), Clerk, null);            // owes 11,500 (invoice 1)
        await using (var db = NewContext(cs))
            await SalesFor(db).SaveAsync(Sale(seed, 1, vat: 0), Clerk, null);            // owes 23,000 in total (invoice 2)

        await using var db3 = NewContext(cs);
        var r = await SalesFor(db3).SaveAsync(Sale(seed, 1, vat: 0, paid: 20000), Clerk, null);

        Assert.Equal(23000m, r.PreviousBalance);
        Assert.Equal(8500m, r.AppliedToPreviousBalance);      // 20,000 − 11,500 for today's invoice
        Assert.Equal(14500m, r.RemainingBalance);              // 34,500 owed − 20,000 paid
        await using var check = NewContext(cs);
        var invoices = await check.Invoices.OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(("Partial", 8500m), (invoices[0].Status, invoices[0].AmountPaid));
        Assert.Equal("Unpaid", invoices[1].Status);
        Assert.Equal("Paid", invoices[2].Status);
        Assert.Equal(14500m, (await check.Customers.SingleAsync()).Balance);
    }
}
