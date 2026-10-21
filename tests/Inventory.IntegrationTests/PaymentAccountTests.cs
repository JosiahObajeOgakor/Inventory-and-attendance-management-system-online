using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Payments;
using Inventory.Application.Purchasing;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>"Josiah paid ₦50,000 to 1234567891, ChewyPets Feeds, Moniepoint, by bank transfer" — that record is kept, for money in (sales, customer payments, AlatPay) and money out (purchases).</summary>
[Collection("mysql")]
public class PaymentAccountTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private const string Moniepoint = "1234567891", FirstBank = "2047950632";

    private sealed class Rig
    {
        public required string Cs; public required Seed Seed; public required SalesService Sales; public required CustomerPaymentService Payments; public required PurchaseService Purchases;
        public required PaymentLinkService Pay; public required FakeVirtualAccounts Va; public required QuotationService Quotes;
    }

    private async Task<Rig> BuildAsync(int stock = 20)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock);
        var db = NewContext(cs);
        List<BankDefault> banks = [new("Moniepoint", "ChewyPets Feeds", Moniepoint), new("First Bank PLC", "ChewyPets Farm & Feeds", FirstBank)];
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", false, false, banks, seed.WarehouseId));
        var tx = Wire.Tx(db);
        var accounts = new PaymentAccountService(new CompanyProfileService(db, co, Clock));
        var stockSvc = new StockService(db, Clock);
        var sales = new SalesService(db, tx, stockSvc, Clock, co, new SaleRequestValidator(), accounts);
        var payments = new CustomerPaymentService(db, tx, Clock, accounts);
        var purchases = new PurchaseService(db, tx, stockSvc, Clock, co, new PurchaseRequestValidator(), accounts);
        var quotes = new QuotationService(db, tx, Clock, co, sales, new NoUsers());
        var va = new FakeVirtualAccounts();
        var pay = new PaymentLinkService(db, tx, Clock, co, new FakeGateway(false), new RecordingNotifier(), payments, quotes, va);
        return new Rig { Cs = cs, Seed = seed, Sales = sales, Payments = payments, Purchases = purchases, Pay = pay, Va = va, Quotes = quotes };
    }

    private static SaleRequest BankSale(Rig r, string? account, decimal paid = 50000, string? payer = null)
    {
        var req = Sale(r.Seed, 5, vat: 0, paid: paid);
        req.PaymentMethod = "Bank Transfer"; req.AccountNumber = account; req.PayerName = payer;
        return req;
    }

    [Fact]
    public async Task A_bank_transfer_sale_keeps_the_account_it_was_paid_into_and_who_paid()
    {
        var r = await BuildAsync();
        var result = await r.Sales.SaveAsync(BankSale(r, Moniepoint, payer: "Josiah"), Clerk, null);

        await using var db = NewContext(r.Cs);
        var p = await db.Payments.AsNoTracking().SingleAsync(x => x.InvoiceId == result.InvoiceId);
        Assert.Equal("Bank Transfer", p.Method);
        Assert.Equal(50000m, p.Amount);
        Assert.Equal("Moniepoint", p.BankName);
        Assert.Equal("ChewyPets Feeds", p.AccountName);
        Assert.Equal(Moniepoint, p.AccountNumber);
        Assert.Equal("Josiah", p.PayerName);

        var detail = await new Inventory.Application.Queries.SalesQueries(db, new NoUsers()).InvoiceAsync(result.InvoiceId, true, default);
        var shown = Assert.Single(detail!.Payments);
        Assert.Equal((Moniepoint, "Josiah"), (shown.AccountNumber, shown.PayerName));
    }

    [Fact]
    public async Task The_payer_defaults_to_the_customer_and_a_bank_transfer_without_an_account_is_refused()
    {
        var r = await BuildAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => r.Sales.SaveAsync(BankSale(r, null), Clerk, null));
        Assert.Contains("Choose which of your accounts", ex.Message);
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Sales.SaveAsync(BankSale(r, "0000000000"), Clerk, null));   // not one of the company's accounts

        await using (var db = NewContext(r.Cs)) { Assert.Empty(await db.Invoices.AsNoTracking().ToListAsync()); Assert.Equal(20, await db.StockBatches.SumAsync(b => b.QuantityOnHand)); }   // nothing was half-saved

        var ok = await r.Sales.SaveAsync(BankSale(r, FirstBank), Clerk, null);
        await using var check = NewContext(r.Cs);
        Assert.Equal("PetMart", (await check.Payments.AsNoTracking().SingleAsync(x => x.InvoiceId == ok.InvoiceId)).PayerName);
    }

    [Fact]
    public async Task Cash_and_unpaid_sales_need_no_account_and_record_none()
    {
        var r = await BuildAsync();
        var cash = Sale(r.Seed, 1, vat: 0, paid: 11500); cash.AccountNumber = Moniepoint;            // an account sent with cash is ignored, not stored
        var a = await r.Sales.SaveAsync(cash, Clerk, null);
        var credit = BankSale(r, null, paid: 0);                                                       // a bank-transfer sale where nothing was paid yet
        var b = await r.Sales.SaveAsync(credit, Clerk, null);

        await using var db = NewContext(r.Cs);
        Assert.Null((await db.Payments.AsNoTracking().SingleAsync(x => x.InvoiceId == a.InvoiceId)).AccountNumber);
        Assert.Empty(await db.Payments.AsNoTracking().Where(x => x.InvoiceId == b.InvoiceId).ToListAsync());
    }

    [Fact]
    public async Task A_later_customer_payment_records_the_account_and_a_named_sender()
    {
        var r = await BuildAsync();
        var sale = await r.Sales.SaveAsync(Sale(r.Seed, 4, vat: 0, paid: 0), Clerk, null);      // 46,000 owed on credit
        await r.Payments.RecordAsync(r.Seed.CustomerId, 20000, "Bank Transfer", FirstBank, "Josiah", Clerk);

        await using var db = NewContext(r.Cs);
        var p = await db.Payments.AsNoTracking().SingleAsync(x => x.InvoiceId == sale.InvoiceId);
        Assert.Equal(("First Bank PLC", "ChewyPets Farm & Feeds", FirstBank, "Josiah"), (p.BankName, p.AccountName, p.AccountNumber, p.PayerName));
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Payments.RecordAsync(r.Seed.CustomerId, 1000, "Bank Transfer", null, null, Clerk));
    }

    [Fact]
    public async Task Editing_the_company_account_list_later_does_not_change_what_old_payments_say()
    {
        var r = await BuildAsync();
        var result = await r.Sales.SaveAsync(BankSale(r, Moniepoint), Clerk, null);
        await using (var db = NewContext(r.Cs))
        {
            await db.CompanyBanks.ExecuteDeleteAsync();                                              // the account was closed and removed from the list
        }
        await using var check = NewContext(r.Cs);
        var p = await check.Payments.AsNoTracking().SingleAsync(x => x.InvoiceId == result.InvoiceId);
        Assert.Equal((Moniepoint, "Moniepoint"), (p.AccountNumber, p.BankName));
    }

    [Fact]
    public async Task A_purchase_payment_records_which_company_account_the_money_was_sent_from()
    {
        var r = await BuildAsync();
        int supplierId;
        await using (var db = NewContext(r.Cs)) { var s = new Supplier { Name = "Agro Mills Ltd" }; db.Suppliers.Add(s); await db.SaveChangesAsync(); supplierId = s.Id; }

        // Part paid up front from the Moniepoint account…
        var po = await r.Purchases.SaveAsync(new PurchaseRequest
        {
            SupplierId = supplierId, VatRate = 0, PaidNow = 30000, PaymentMethod = "Bank Transfer", AccountNumber = Moniepoint,
            Lines = [new PurchaseLineDto { ProductId = r.Seed.ProductId, Quantity = 10, UnitCost = 8500 }],
        }, Clerk, null);
        // …the rest later from First Bank.
        await r.Purchases.PayAsync(po.PurchaseOrderId, "Bank Transfer", FirstBank, Clerk);

        await using var check = NewContext(r.Cs);
        var rows = await check.SupplierPayments.AsNoTracking().Where(x => x.PurchaseOrderId == po.PurchaseOrderId).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((30000m, "Moniepoint", Moniepoint), (rows[0].Amount, rows[0].BankName, rows[0].AccountNumber));
        Assert.Equal((55000m, "First Bank PLC", FirstBank), (rows[1].Amount, rows[1].BankName, rows[1].AccountNumber));
        var detail = await new Inventory.Application.Queries.PurchaseQueries(check).GetAsync(po.PurchaseOrderId, default);
        Assert.Equal(2, detail!.Payments.Count);

        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Purchases.SaveAsync(new PurchaseRequest
        {
            SupplierId = supplierId, VatRate = 0, PaidNow = 1000, PaymentMethod = "Bank Transfer", AccountNumber = null,
            Lines = [new PurchaseLineDto { ProductId = r.Seed.ProductId, Quantity = 1, UnitCost = 8500 }],
        }, Clerk, null));
    }

    [Fact]
    public async Task A_payment_made_into_an_alatpay_account_in_the_chat_records_that_account_and_the_customer()
    {
        var r = await BuildAsync();
        var customer = await new Inventory.Application.SalesAssistant.CustomerLookupService(NewContext(r.Cs)).FindOrCreateAsync("2348011111111", "Ngozi Eze", default);
        var quote = await r.Quotes.CreateAsync(new QuoteRequest
        {
            CustomerId = customer.Id, VatRate = 0, Lines = [new SaleLineDto { ProductId = r.Seed.ProductId, Quantity = 2, UnitPrice = 11500 }],
        }, new CurrentUser(0, "Sales Assistant", "SYSTEM"));
        var acct = await r.Pay.GetOrCreateVirtualAccountAsync(quote.Id, quote.Number, quote.Total, customer.Name, customer.Phone, null, default);
        var link = await NewContext(r.Cs).PaymentLinks.AsNoTracking().SingleAsync();
        r.Va.Status[link.ExternalId!] = new TransferStatus(true, quote.Total, quote.Total, "completed");

        var settled = await r.Pay.SettleAsync(link.Reference, default);
        Assert.True(settled.Applied);
        await using var db = NewContext(r.Cs);
        var p = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal("AlatPay", p.Method);
        Assert.Equal(acct!.AccountNumber, p.AccountNumber);               // the virtual account the transfer landed in
        Assert.Equal("Test Co Ltd", p.AccountName);
        Assert.Equal("Ngozi Eze", p.PayerName);
    }
}
