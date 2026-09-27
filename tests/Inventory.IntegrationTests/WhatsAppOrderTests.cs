using Inventory.Application.Company;
using Inventory.Application.Documents;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Payments;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>Records what would have gone out over WhatsApp instead of calling Meta.</summary>
internal sealed class RecordingWhatsApp(bool configured = true, bool templatesApproved = true) : IWhatsAppSender
{
    public List<(string To, string Template, IReadOnlyList<string> Params)> Templates { get; } = [];
    public List<(string To, string Filename)> Documents { get; } = [];
    public bool IsConfigured(string companyKey) => configured;
    public Task SendTextAsync(string companyKey, string toE164, string text, CancellationToken ct) => Task.CompletedTask;
    public Task SendDocumentAsync(string companyKey, string toE164, byte[] pdf, string filename, string caption, CancellationToken ct)
    { Documents.Add((toE164, filename)); return Task.CompletedTask; }
    public Task SendButtonsAsync(string companyKey, string toE164, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> SendTemplateAsync(string companyKey, string toE164, string templateName, string languageCode, IReadOnlyList<string> bodyParameters, CancellationToken ct)
    { if (templatesApproved) Templates.Add((toE164, templateName, bodyParameters)); return Task.FromResult(templatesApproved); }
}

[Collection("mysql")]
public class WhatsAppOrderTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();

    private static readonly FulfilmentOptions Fulfilment = new()
    {
        AdminWhatsApp = "07039986047", RiderName = "Musa Rider", RiderPhone = "08011112222",
    };

    /// <summary>A quotation the way the sales assistant makes it: retail price, VAT, a delivery fee and address, and a fulfilment warehouse.</summary>
    private static async Task<(int Id, decimal Total)> QuoteWithDelivery(string cs, Seed seed, int qty = 2)
    {
        await using var db = NewContext(cs);
        var q = await Wire.Quotes(db).CreateAsync(new QuoteRequest
        {
            CustomerId = seed.CustomerId, VatRate = 7.5m, DeliveryFee = 2000m, DeliveryZone = "Mainland", DeliveryAddress = "12 Allen Avenue, Ikeja", WarehouseId = seed.WarehouseId,
            Lines = [new SaleLineDto { ProductId = seed.ProductId, Quantity = qty, UnitPrice = 11500 }],
        }, Clerk);
        return (q.Id, q.Total);
    }

    private static OrderDispatchService Dispatch(BusinessDbContext db, IWhatsAppSender wa, params IPaymentGateway[] gateways)
    {
        var co = Wire.Co();
        var profile = new CompanyProfileService(db, co, Clock);
        var docs = new DocumentQueries(db, profile, co, new NoUsers(), Clock, Pay.Svc(db, new RecordingNotifier(), gateways));
        var receipts = new PaymentFollowUpService(docs, new QuestDocumentRenderer(), wa, co, NullLogger<PaymentFollowUpService>.Instance);
        return new OrderDispatchService(db, receipts, Wire.Waybills(db), wa, co, Clock, Options.Create(Fulfilment), NullLogger<OrderDispatchService>.Instance);
    }

    [Fact]
    public async Task Delivery_is_added_after_VAT_and_carried_onto_the_invoice()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var (qid, total) = await QuoteWithDelivery(cs, seed);
        Assert.Equal(23000m + 1725m + 2000m, total);   // goods + 7.5% VAT on goods only + delivery

        await using (var db = NewContext(cs))
            await Wire.Quotes(db).ConvertAsync(qid, new ConvertQuoteRequest { WarehouseId = seed.WarehouseId, PaymentMethod = "Card" }, Clerk);
        await using var check = NewContext(cs);
        var inv = await check.Invoices.SingleAsync();
        Assert.Equal((26725m, 1725m, 2000m, "Mainland", "12 Allen Avenue, Ikeja"), (inv.TotalAmount, inv.VatAmount, inv.DeliveryFee, inv.DeliveryZone, inv.DeliveryAddress));
        Assert.Equal(26725m, (await check.Customers.SingleAsync()).Balance);   // the fee is owed like the goods
    }

    [Fact]
    public async Task The_customer_can_pay_with_AlatPay_and_the_sale_records_AlatPay_and_the_channel()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var (qid, total) = await QuoteWithDelivery(cs, seed);
        var paystack = new FakeGateway(); var alat = new FakeGateway(provider: PaymentProviders.AlatPay); var note = new RecordingNotifier();

        PayLink link;
        await using (var db = NewContext(cs))
        {
            var svc = Pay.Svc(db, note, paystack, alat);
            Assert.Equal(new[] { PaymentProviders.Paystack, PaymentProviders.AlatPay }, svc.Providers);
            link = (await svc.GetOrCreateAsync(PaymentDocTypes.Quotation, qid, "Q-1", total, null, "PetMart", PaymentProviders.AlatPay, default))!;
        }
        Assert.Equal(PaymentProviders.AlatPay, link.Provider);
        Assert.Equal((0, 1), (paystack.Initialized, alat.Initialized));   // only the chosen processor was asked
        Assert.Equal(2_672_500L, alat.LastKobo);

        // Choosing Paystack afterwards gives a separate Paystack link; asking for AlatPay again reuses the first.
        await using (var db = NewContext(cs))
        {
            var svc = Pay.Svc(db, note, paystack, alat);
            var ps = await svc.GetOrCreateAsync(PaymentDocTypes.Quotation, qid, "Q-1", total, null, "PetMart", PaymentProviders.Paystack, default);
            Assert.Equal(PaymentProviders.Paystack, ps!.Provider);
            Assert.Equal(link.Reference, (await svc.GetOrCreateAsync(PaymentDocTypes.Quotation, qid, "Q-1", total, null, "PetMart", PaymentProviders.AlatPay, default))!.Reference);
        }
        Assert.Equal(1, alat.Initialized);

        var stored = await NewContext(cs).PaymentLinks.SingleAsync(l => l.Reference == link.Reference);
        Assert.StartsWith("pay", stored.ProviderReference);            // AlatPay's own reference is what gets verified
        alat.ByReference[stored.ProviderReference!] = new GatewayVerification(true, 2_672_500, "NGN", "Bank Transfer");

        await using (var db = NewContext(cs)) Assert.True((await Pay.Svc(db, note, paystack, alat).SettleAsync(link.Reference, default)).Applied);
        Assert.Contains(stored.ProviderReference!, alat.Verified);
        Assert.Empty(paystack.Verified);

        await using var check = NewContext(cs);
        Assert.Equal("AlatPay", (await check.Payments.SingleAsync()).Method);
        var paid = await check.PaymentLinks.SingleAsync(l => l.Reference == link.Reference);
        Assert.Equal((PaymentLinkStatuses.Paid, "Bank Transfer"), (paid.Status, paid.Channel));
        Assert.Equal(8, await check.StockBatches.SumAsync(b => b.QuantityOnHand));
        Assert.Contains("via AlatPay", Assert.Single(note.Sent).Message);
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task Pending_links_are_rechecked_and_only_the_ones_the_processor_confirms_are_settled()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var (q1, t1) = await QuoteWithDelivery(cs, seed, qty: 1);
        var (q2, t2) = await QuoteWithDelivery(cs, seed, qty: 1);
        var alat = new FakeGateway(provider: PaymentProviders.AlatPay); var note = new RecordingNotifier();
        await using (var db = NewContext(cs))
        {
            var svc = Pay.Svc(db, note, alat);
            await svc.GetOrCreateAsync(PaymentDocTypes.Quotation, q1, "Q-1", t1, null, "A", PaymentProviders.AlatPay, default);
            await svc.GetOrCreateAsync(PaymentDocTypes.Quotation, q2, "Q-2", t2, null, "B", PaymentProviders.AlatPay, default);
        }
        var first = await NewContext(cs).PaymentLinks.SingleAsync(l => l.DocId == q1);
        alat.ByReference[first.ProviderReference!] = new GatewayVerification(true, (long)(t1 * 100), "NGN", "Card");

        IReadOnlyList<SettleResult> settled;
        await using (var db = NewContext(cs)) settled = await Pay.Svc(db, note, alat).SettlePendingAsync(PaymentProviders.AlatPay, DateTime.UtcNow.AddDays(-1), default);
        Assert.Single(settled);

        await using var check = NewContext(cs);
        Assert.Equal(PaymentLinkStatuses.Paid, (await check.PaymentLinks.SingleAsync(l => l.DocId == q1)).Status);
        Assert.Equal(PaymentLinkStatuses.Pending, (await check.PaymentLinks.SingleAsync(l => l.DocId == q2)).Status);
        Assert.Single(await check.Invoices.ToListAsync());

        // A second sweep (a retried webhook, the reconciler) changes nothing.
        await using (var db = NewContext(cs)) Assert.Empty(await Pay.Svc(db, note, alat).SettlePendingAsync(null, DateTime.UtcNow.AddDays(-1), default));
    }

    [Fact]
    public async Task A_paid_order_gets_a_receipt_one_waybill_for_the_rider_and_WhatsApp_messages_to_admin_and_rider()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using (var db = NewContext(cs)) await db.Customers.ExecuteUpdateAsync(u => u.SetProperty(c => c.Phone, "2348030000000"));
        var (qid, total) = await QuoteWithDelivery(cs, seed);
        var alat = new FakeGateway(provider: PaymentProviders.AlatPay); var note = new RecordingNotifier(); var wa = new RecordingWhatsApp();

        string reference;
        await using (var db = NewContext(cs)) reference = (await Pay.Svc(db, note, alat).GetOrCreateAsync(PaymentDocTypes.Quotation, qid, "Q-1", total, null, "PetMart", PaymentProviders.AlatPay, default))!.Reference;
        alat.Verification = new GatewayVerification(true, (long)(total * 100), "NGN", "Card");
        SettleResult result;
        await using (var db = NewContext(cs)) result = await Pay.Svc(db, note, alat).SettleAsync(reference, default);
        Assert.Equal((PaymentProviders.AlatPay, reference), (result.Provider, result.Reference));

        await using (var db = NewContext(cs)) await Dispatch(db, wa, alat).CompleteAsync(result, default);

        Assert.Equal("2348030000000", Assert.Single(wa.Documents).To);                     // customer's receipt
        var admin = Assert.Single(wa.Templates, t => t.Template == "order_paid_admin");
        Assert.Equal("2347039986047", admin.To);
        Assert.Contains("AlatPay (Card)", admin.Params);
        Assert.Contains("12 Allen Avenue, Ikeja", admin.Params);
        Assert.Contains("Mainland", admin.Params);
        var rider = Assert.Single(wa.Templates, t => t.Template == "dispatch_new_order");
        Assert.Equal("2348011112222", rider.To);
        Assert.Contains("2 x ", rider.Params[2]);
        Assert.Contains("+2348030000000", rider.Params);

        await using (var check = NewContext(cs))
        {
            var w = await check.Waybills.SingleAsync();
            Assert.Equal(("Musa Rider", "08011112222", "12 Allen Avenue, Ikeja"), (w.DriverName, w.DriverPhone, w.DestinationAddress));
            Assert.Equal(w.WaybillNumber, rider.Params[0]);
            var link = await check.PaymentLinks.SingleAsync();
            Assert.NotNull(link.AdminNotifiedAt); Assert.NotNull(link.RiderNotifiedAt);
        }

        // Running the completion again (e.g. the reconciler racing a webhook) never makes a second waybill.
        await using (var db = NewContext(cs)) await Dispatch(db, new RecordingWhatsApp(), alat).CompleteAsync(result, default);
        Assert.Single(await NewContext(cs).Waybills.ToListAsync());
    }

    [Fact]
    public async Task An_unapproved_template_leaves_the_payment_recorded_and_the_missed_message_visible()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        var (qid, total) = await QuoteWithDelivery(cs, seed);
        var ps = new FakeGateway(); var note = new RecordingNotifier();
        string reference;
        await using (var db = NewContext(cs)) reference = (await Pay.Svc(db, note, ps).GetOrCreateAsync(PaymentDocTypes.Quotation, qid, "Q-1", total, null, "PetMart", PaymentProviders.Paystack, default))!.Reference;
        ps.Verification = new GatewayVerification(true, (long)(total * 100), "NGN", "card");
        SettleResult result;
        await using (var db = NewContext(cs)) result = await Pay.Svc(db, note, ps).SettleAsync(reference, default);
        await using (var db = NewContext(cs)) await Dispatch(db, new RecordingWhatsApp(templatesApproved: false), ps).CompleteAsync(result, default);

        await using var check = NewContext(cs);
        Assert.Equal("Paid", (await check.Invoices.SingleAsync()).Status);
        Assert.Single(await check.Waybills.ToListAsync());   // the rider's waybill still exists for the admin to hand over
        var link = await check.PaymentLinks.SingleAsync();
        Assert.Null(link.AdminNotifiedAt); Assert.Null(link.RiderNotifiedAt);
    }

    [Fact]
    public void AlatPay_webhooks_are_Base64_HMAC_SHA256_of_the_raw_body_and_forgeries_are_refused()
    {
        const string secret = "whsec_test_123", body = "{\"Value\":{\"Data\":{\"Status\":\"completed\"}}}";
        var good = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(body)));
        Assert.True(AlatPayGateway.IsValidSignature(secret, body, good));
        Assert.False(AlatPayGateway.IsValidSignature(secret, body + " ", good));   // re-serialised / tampered body
        Assert.False(AlatPayGateway.IsValidSignature(secret, body, good.ToLowerInvariant()));
        Assert.False(AlatPayGateway.IsValidSignature(secret, body, null));
        Assert.False(AlatPayGateway.IsValidSignature("", body, good));            // no secret configured
    }

    [Fact]
    public void AlatPay_status_counts_as_paid_only_when_link_and_transaction_are_completed()
    {
        var paid = AlatPayGateway.ParseStatus("""{"data":{"status":"completed","amount":100.00,"currency":"NGN","transactions":{"amount":100.00,"channel":"Card","currency":"NGN","status":"completed"}},"status":true}""");
        Assert.Equal((true, 10_000L, "NGN", "Card"), (paid!.Success, paid.AmountKobo, paid.Currency, paid.Channel));
        var pending = AlatPayGateway.ParseStatus("""{"data":{"status":"pending","amount":100.00,"currency":"NGN","transactions":null},"status":true}""");
        Assert.False(pending!.Success);
        var halfDone = AlatPayGateway.ParseStatus("""{"data":{"status":"completed","amount":100.00,"transactions":{"amount":100,"status":"failed"}}}""");
        Assert.False(halfDone!.Success);
        Assert.Null(AlatPayGateway.ParseStatus("""{"status":false,"message":"Not found","data":null}"""));
    }

    [Fact]
    public async Task Delivery_zones_start_with_Lagos_are_managed_by_admins_and_match_by_name_ignoring_case()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var zones = new DeliveryZoneService(db, Clock);
        var seeded = Assert.Single(await zones.ListAsync(true));
        Assert.Equal(("Lagos", 3000m), (seeded.Name, seeded.Fee));

        await zones.CreateAsync(new DeliveryZoneInput { Name = "Ogun", Fee = 4500 }, Wire.Admin);
        var abuja = await zones.CreateAsync(new DeliveryZoneInput { Name = "Abuja", Fee = 9000 }, Wire.Admin);
        await Assert.ThrowsAnyAsync<Exception>(() => zones.CreateAsync(new DeliveryZoneInput { Name = "ogun", Fee = 1 }, Wire.Admin));   // names are unique
        await zones.UpdateAsync(abuja, new DeliveryZoneInput { Name = "Abuja", Fee = 9500, IsActive = false }, Wire.Admin);

        Assert.Equal(4500m, (await zones.FindActiveAsync(" OGUN "))!.Fee);
        Assert.Equal(3000m, (await zones.FindActiveAsync("lagos"))!.Fee);
        Assert.Null(await zones.FindActiveAsync("Abuja"));     // switched off: the assistant won't offer it
        Assert.Null(await zones.FindActiveAsync(null));
        Assert.Equal(3, (await zones.ListAsync(false)).Count);
    }

    [Theory]
    [InlineData("07039986047", "2347039986047")]
    [InlineData("+234 703 998 6047", "2347039986047")]
    public void Admin_and_rider_numbers_become_WhatsApp_form(string given, string expected) => Assert.Equal(expected, OrderDispatchService.International(given));
}
