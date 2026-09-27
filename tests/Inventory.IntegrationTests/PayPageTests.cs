using System.Net;
using Inventory.Api.Controllers;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Documents;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>The "choose how to pay" page behind the PAY ONLINE button on receipts and quotations.</summary>
[Collection("mysql")]
public class PayPageTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private static readonly EphemeralDataProtectionProvider Keys = new();

    private static PayPageLinks Links(string site = "https://chewypetsfeeds.com") => new(Keys, Options.Create(new SiteOptions { PublicUrl = site }), Wire.Co());
    private static PayPageService Page(BusinessDbContext db, params IPaymentGateway[] g) =>
        new(db, Pay.Svc(db, new RecordingNotifier(), g), new CompanyProfileService(db, Wire.Co(), Clock), Wire.Co());

    private async Task<(string Cs, Seed Seed, int InvoiceId)> Invoice(decimal paid = 0)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 20);
        await using var s = NewContext(cs);
        return (cs, seed, (await SalesFor(s).SaveAsync(Sale(seed, 2, vat: 0, paid: paid), Clerk, null)).InvoiceId);   // total 23,000
    }

    [Fact]
    public void The_link_names_one_document_of_one_business_and_cannot_be_edited_or_forged()
    {
        var url = Links().UrlFor(PaymentDocTypes.Invoice, 42)!;
        Assert.StartsWith("https://chewypetsfeeds.com/pay/", url);
        var token = url["https://chewypetsfeeds.com/pay/".Length..];
        Assert.True(PayPageLinks.TryRead(Keys, token, out var co, out var type, out var id));
        Assert.Equal(("test", PaymentDocTypes.Invoice, 42), (co, type, id));

        Assert.False(PayPageLinks.TryRead(Keys, token[..^2] + (token[^1] == 'A' ? "BB" : "AA"), out _, out _, out _));   // tampered
        Assert.False(PayPageLinks.TryRead(Keys, "test|Invoice|42", out _, out _, out _));                                   // hand-made
        Assert.False(PayPageLinks.TryRead(new EphemeralDataProtectionProvider(), token, out _, out _, out _));               // another server's keys
        Assert.Null(Links(site: "").UrlFor(PaymentDocTypes.Invoice, 42));      // no site configured → documents fall back to a direct checkout
        Assert.Null(Links(site: "http://insecure.example").UrlFor(PaymentDocTypes.Invoice, 42));
    }

    [Fact]
    public async Task The_page_offers_every_processor_for_what_is_owed_now_and_starts_the_one_chosen()
    {
        var (cs, _, invoiceId) = await Invoice(paid: 3000);
        var paystack = new FakeGateway(); var alat = new FakeGateway(provider: PaymentProviders.AlatPay);
        await using (var db = NewContext(cs))
        {
            var info = await Page(db, paystack, alat).GetAsync(PaymentDocTypes.Invoice, invoiceId, default);
            Assert.Equal(("Receipt", 20000m, (string?)null, false), (info.DocKind, info.Amount, info.Closed, info.Awaiting));
            Assert.Equal([PaymentProviders.Paystack, PaymentProviders.AlatPay], info.Providers);
        }

        string url;
        await using (var db = NewContext(cs)) url = await Page(db, paystack, alat).StartAsync(PaymentDocTypes.Invoice, invoiceId, PaymentProviders.AlatPay, "https://chewypetsfeeds.com/pay/x", default);
        Assert.StartsWith("https://alatpay.example/", url);
        Assert.Equal((0, 1, 2_000_000L), (paystack.Initialized, alat.Initialized, alat.LastKobo!.Value));   // only the chosen one, for the balance
        Assert.Equal("https://chewypetsfeeds.com/pay/x", alat.LastMetadata![PaymentLinkService.ReturnUrlKey]);   // comes back to the page
        Assert.Equal(Wire.Co().LegalName, alat.LastMetadata["business"]);                                        // says which business

        await using (var db = NewContext(cs))
            Assert.True((await Page(db, paystack, alat).GetAsync(PaymentDocTypes.Invoice, invoiceId, default)).Awaiting);   // "started, not yet confirmed"
    }

    [Fact]
    public async Task A_receipt_that_is_paid_or_cancelled_offers_nothing_to_pay()
    {
        var (cs, seed, paidId) = await Invoice(paid: 23000);
        await using (var db = NewContext(cs))
        {
            var info = await Page(db, new FakeGateway()).GetAsync(PaymentDocTypes.Invoice, paidId, default);
            Assert.Contains("paid in full", info.Closed);
            await Assert.ThrowsAsync<BusinessRuleException>(() => Page(db, new FakeGateway()).StartAsync(PaymentDocTypes.Invoice, paidId, PaymentProviders.Paystack, null, default));
        }
        int voided;
        await using (var db = NewContext(cs)) voided = (await SalesFor(db).SaveAsync(Sale(seed, 1, vat: 0), Clerk, null)).InvoiceId;
        await using (var db = NewContext(cs)) await new Services(db).Voids.VoidAsync(voided, "wrong customer", Wire.Admin);
        await using (var check = NewContext(cs))
            Assert.Contains("cancelled", (await Page(check, new FakeGateway()).GetAsync(PaymentDocTypes.Invoice, voided, default)).Closed);
    }

    [Fact]
    public async Task A_quotation_is_payable_while_open_and_closed_once_it_became_a_sale()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 20);
        int qid;
        await using (var db = NewContext(cs))
            qid = (await Wire.Quotes(db).CreateAsync(new QuoteRequest { CustomerId = seed.CustomerId, Lines = [new SaleLineDto { ProductId = seed.ProductId, Quantity = 1, UnitPrice = 11500 }] }, Clerk)).Id;
        await using (var db = NewContext(cs))
            Assert.Equal(("Quotation", 11500m, (string?)null), ((await Page(db, new FakeGateway()).GetAsync(PaymentDocTypes.Quotation, qid, default)) is var i ? (i.DocKind, i.Amount, i.Closed) : default));
        await using (var db = NewContext(cs)) await Wire.Quotes(db).ConvertAsync(qid, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId }, Clerk);
        await using (var check = NewContext(cs))
            Assert.NotNull((await Page(check, new FakeGateway()).GetAsync(PaymentDocTypes.Quotation, qid, default)).Closed);
    }

    [Fact]
    public async Task The_receipt_button_opens_the_payment_page_not_a_single_processor()
    {
        var (cs, _, invoiceId) = await Invoice();
        await using var db = NewContext(cs);
        var co = Wire.Co();
        var docs = new DocumentQueries(db, new CompanyProfileService(db, co, Clock), co, new NoUsers(), Clock, Pay.Svc(db, new RecordingNotifier(), new FakeGateway()), Links());
        var receipt = await docs.ReceiptAsync(invoiceId, default);
        Assert.StartsWith("https://chewypetsfeeds.com/pay/", receipt.PayUrl);
        Assert.Equal(0, await db.PaymentLinks.CountAsync());   // nothing is created until the customer picks a processor
    }
}
