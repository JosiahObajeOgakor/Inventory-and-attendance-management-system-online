using Inventory.Application.Common;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>Deleting a sale undoes it completely: stock back, money back, nothing left pointing at it — and the books still balance.</summary>
[Collection("mysql")]
public class DeleteSaleTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private static InvoiceDeleteService Deletes(BusinessDbContext db) => new(db, Wire.Tx(db), new StockService(db, Clock), Clock);

    private static async Task<int> OnHand(string cs) { await using var db = NewContext(cs); return await db.StockBatches.SumAsync(b => b.QuantityOnHand); }
    private static async Task<decimal> Balance(string cs) { await using var db = NewContext(cs); return (await db.Customers.SingleAsync()).Balance; }
    /// <summary>The customer ledger (Debit = owes more, Credit = owes less) must always add up to the stored balance.</summary>
    private static async Task<decimal> LedgerNet(string cs)
    {
        await using var db = NewContext(cs);
        var rows = await db.Ledger.Where(l => l.CustomerId != null).ToListAsync();
        return rows.Sum(l => l.EntryType == LedgerEntryTypes.Debit ? l.Amount : -l.Amount);
    }

    [Fact]
    public async Task Deleting_a_part_paid_sale_returns_the_stock_refunds_what_was_paid_and_clears_what_was_owed()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        SaleResult sale;
        await using (var db = NewContext(cs)) sale = await SalesFor(db).SaveAsync(Sale(seed, 2, vat: 0, paid: 5000), Clerk, null);   // 23,000, 5,000 paid
        Assert.Equal((8, 18000m), (await OnHand(cs), await Balance(cs)));

        InvoiceDeleteResult preview, r;
        await using (var db = NewContext(cs)) preview = await Deletes(db).PreviewAsync(sale.InvoiceId, default);
        await using (var db = NewContext(cs)) r = await Deletes(db).DeleteAsync(sale.InvoiceId, "Customer cancelled the order", Wire.Admin);
        Assert.Equal((5000m, 0m, 18000m), (preview.RefundDue, preview.CreditRestored, preview.OwedCleared));
        Assert.Equal((r.RefundDue, r.OwedCleared, Assert.Single(r.StockReturned).Quantity), (5000m, 18000m, 2));

        Assert.Equal((10, 0m), (await OnHand(cs), await Balance(cs)));   // stock and balance exactly as before the sale
        Assert.Equal(await Balance(cs), await LedgerNet(cs));            // and the ledger agrees
        await using var check = NewContext(cs);
        Assert.Empty(await check.Invoices.ToListAsync());
        Assert.Empty(await check.Payments.ToListAsync());                // the 5,000 no longer counts as income
        Assert.Empty(await check.RebateEntries.ToListAsync());
        Assert.Contains(await check.StockMovements.ToListAsync(), m => m.ReferenceType == MovementReferences.InvoiceDelete && m.Quantity == 2);
        var log = await check.AuditLogs.SingleAsync(a => a.Action == "INVOICE_DELETED");
        Assert.Contains("Customer cancelled the order", log.Detail);
        Assert.Contains(sale.InvoiceNumber, log.Detail);                 // a full copy of what was deleted
    }

    [Fact]
    public async Task Credit_that_paid_for_the_sale_goes_back_on_the_customers_account()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10, balance: -30000);      // the customer holds ₦30,000 credit
        SaleResult sale;
        await using (var db = NewContext(cs)) sale = await SalesFor(db).SaveAsync(Sale(seed, 2, vat: 0), Clerk, null);   // 23,000 taken from credit
        Assert.Equal(-7000m, await Balance(cs));

        InvoiceDeleteResult r;
        await using (var db = NewContext(cs)) r = await Deletes(db).DeleteAsync(sale.InvoiceId, "Wrong customer", Wire.Admin);
        Assert.Equal((0m, 23000m), (r.RefundDue, r.CreditRestored));     // no cash to hand back: the credit returns
        Assert.Equal(-30000m, await Balance(cs));
    }

    [Fact]
    public async Task Its_waybill_goes_with_it_the_quotation_reopens_and_unused_pay_links_stop_working()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        int qid; SaleResult sale;
        await using (var db = NewContext(cs))
            qid = (await Wire.Quotes(db).CreateAsync(new QuoteRequest { CustomerId = seed.CustomerId, Lines = [new SaleLineDto { ProductId = seed.ProductId, Quantity = 1, UnitPrice = 11500 }] }, Clerk)).Id;
        await using (var db = NewContext(cs)) sale = await Wire.Quotes(db).ConvertAsync(qid, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId }, Clerk);
        await using (var db = NewContext(cs)) await Wire.Waybills(db).CreateAsync(new WaybillInput { InvoiceId = sale.InvoiceId, DriverName = "Musa" }, Clerk);
        var gw = new FakeGateway(); var note = new RecordingNotifier();
        await using (var db = NewContext(cs)) await Pay.Svc(db, note, gw).GetOrCreateAsync(PaymentDocTypes.Invoice, sale.InvoiceId, sale.InvoiceNumber, 11500m, null, "PetMart", default);

        await using (var db = NewContext(cs)) await Deletes(db).DeleteAsync(sale.InvoiceId, "Duplicate", Wire.Admin);

        await using (var check = NewContext(cs))
        {
            Assert.Empty(await check.Waybills.ToListAsync());
            var q = await check.Quotations.SingleAsync();
            Assert.Equal((QuotationStatuses.Open, (int?)null), (q.Status, q.ConvertedInvoiceId));
            Assert.Equal(PaymentLinkStatuses.Cancelled, (await check.PaymentLinks.SingleAsync()).Status);
        }

        // The customer pays on the old link anyway: nothing is recorded against a sale that no longer exists, and the admin is told to refund.
        gw.Verification = new GatewayVerification(true, 1_150_000, "NGN", "card");
        var reference = await NewContext(cs).PaymentLinks.Select(l => l.Reference).SingleAsync();
        await using (var db = NewContext(cs)) Assert.False((await Pay.Svc(db, note, gw).SettleAsync(reference, default)).Applied);
        await using (var db = NewContext(cs)) await Pay.Svc(db, note, gw).SettleAsync(reference, default);   // a webhook retry doesn't alert twice
        Assert.Contains("refund", Assert.Single(note.Sent).Message);
        Assert.Empty(await NewContext(cs).Payments.ToListAsync());
    }

    [Fact]
    public async Task A_voided_sale_can_still_be_deleted_and_only_its_paid_money_is_refunded()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        SaleResult sale;
        await using (var db = NewContext(cs)) sale = await SalesFor(db).SaveAsync(Sale(seed, 2, vat: 0, paid: 5000), Clerk, null);
        await using (var db = NewContext(cs)) await new Services(db).Voids.VoidAsync(sale.InvoiceId, "old way", Wire.Admin);
        Assert.Equal((10, 0m), (await OnHand(cs), await Balance(cs)));

        InvoiceDeleteResult r;
        await using (var db = NewContext(cs)) r = await Deletes(db).DeleteAsync(sale.InvoiceId, "Clean up", Wire.Admin);
        Assert.Equal((5000m, 0m), (r.RefundDue, r.OwedCleared));
        Assert.Empty(r.StockReturned);                                     // the void already put it back
        Assert.Equal((10, 0m), (await OnHand(cs), await Balance(cs)));
        Assert.Equal(await Balance(cs), await LedgerNet(cs));
    }

    [Fact]
    public async Task A_sale_whose_rebate_was_redeemed_cannot_be_deleted_and_nothing_changes()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        SaleResult sale;
        await using (var db = NewContext(cs)) sale = await SalesFor(db).SaveAsync(Sale(seed, 2, vat: 0, paid: 23000), Clerk, null);
        await using (var db = NewContext(cs)) await db.RebateEntries.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "Redeemed"));

        await using (var db = NewContext(cs))
            await Assert.ThrowsAsync<BusinessRuleException>(() => Deletes(db).DeleteAsync(sale.InvoiceId, "try", Wire.Admin));
        Assert.Equal(8, await OnHand(cs));
        Assert.Single(await NewContext(cs).Invoices.ToListAsync());
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        SaleResult sale;
        await using (var db = NewContext(cs)) sale = await SalesFor(db).SaveAsync(Sale(seed, 1), Clerk, null);
        await using var d = NewContext(cs);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Deletes(d).DeleteAsync(sale.InvoiceId, "  ", Wire.Admin));
    }
}
