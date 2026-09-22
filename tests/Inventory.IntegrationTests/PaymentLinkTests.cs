using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Payments;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class FakeGateway(bool configured = true) : IPaymentGateway
{
    public bool IsConfigured => configured;
    public string FallbackEmail => "admin@example.com";
    public int Initialized { get; private set; }
    public string? LastEmail { get; private set; }
    public long? LastKobo { get; private set; }
    public GatewayVerification? Verification { get; set; }
    public Task<GatewayCharge> InitializeAsync(string email, long amountKobo, string reference, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    { Initialized++; LastEmail = email; LastKobo = amountKobo; return Task.FromResult(new GatewayCharge("https://checkout.paystack.com/" + reference.Replace("-", ""), reference)); }
    public Task<GatewayVerification?> VerifyAsync(string reference, CancellationToken ct) => Task.FromResult(Verification);
    public bool IsValidSignature(string rawBody, string? signature) => signature == "ok";
}

internal sealed class RecordingNotifier : IAdminNotifier
{
    public List<(string Subject, string Message)> Sent { get; } = [];
    public bool Fail { get; set; }
    public Task NotifyAsync(string subject, string message, CancellationToken ct) { if (Fail) throw new InvalidOperationException("down"); Sent.Add((subject, message)); return Task.CompletedTask; }
}

[Collection("mysql")]
public class PaymentLinkTests(MySqlFixture mysql)
{
    private static PaymentLinkService Svc(BusinessDbContext db, IPaymentGateway g, IAdminNotifier n)
    {
        var clock = new SystemClock();
        return new PaymentLinkService(db, Wire.Tx(db), clock, Wire.Co(), g, n, new CustomerPaymentService(db, Wire.Tx(db), clock));
    }

    private async Task<(string Cs, Seed Seed, int InvoiceId)> Invoice(decimal paid = 0)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 20);
        await using var s = NewContext(cs);
        var r = await SalesFor(s).SaveAsync(Sale(seed, 2, vat: 0, paid: paid), Clerk, null);   // total 23,000
        return (cs, seed, r.InvoiceId);
    }

    [Fact]
    public async Task A_link_is_for_the_exact_amount_in_kobo_and_is_reused_until_the_amount_changes()
    {
        var (cs, seed, inv) = await Invoice();
        var gw = new FakeGateway();
        await using var db = NewContext(cs);
        var a = await Svc(db, gw, new RecordingNotifier()).GetOrCreateAsync("Invoice", inv, "INV-1", 23000m, "buyer@petmart.example", "PetMart", default);
        Assert.NotNull(a);
        Assert.Equal(2_300_000L, gw.LastKobo);
        Assert.Equal("buyer@petmart.example", gw.LastEmail);
        Assert.StartsWith("test-I", a!.Reference);                         // company key first, so a webhook can be routed

        await using var db2 = NewContext(cs);
        var b = await Svc(db2, gw, new RecordingNotifier()).GetOrCreateAsync("Invoice", inv, "INV-1", 23000m, "buyer@petmart.example", "PetMart", default);
        Assert.Equal(a.Reference, b!.Reference);
        Assert.Equal(1, gw.Initialized);                                   // no second call to Paystack

        await using var db3 = NewContext(cs);
        var c = await Svc(db3, gw, new RecordingNotifier()).GetOrCreateAsync("Invoice", inv, "INV-1", 13000m, null, "PetMart", default);
        Assert.NotEqual(a.Reference, c!.Reference);                        // part-paid: a new link for what is still owed
        Assert.Equal("admin@example.com", gw.LastEmail);                   // no customer email: the fallback keeps the link possible
    }

    [Fact]
    public async Task No_link_when_payments_are_not_set_up_or_nothing_is_owed()
    {
        var (cs, _, inv) = await Invoice();
        await using var db = NewContext(cs);
        Assert.Null(await Svc(db, new FakeGateway(configured: false), new RecordingNotifier()).GetOrCreateAsync("Invoice", inv, "INV-1", 23000m, null, "x", default));
        Assert.Null(await Svc(db, new FakeGateway(), new RecordingNotifier()).GetOrCreateAsync("Invoice", inv, "INV-1", 0m, null, "x", default));
    }

    [Fact]
    public async Task A_confirmed_payment_settles_the_invoice_once_updates_the_customer_and_tells_the_admin_who_paid()
    {
        var (cs, seed, inv) = await Invoice();
        var gw = new FakeGateway(); var note = new RecordingNotifier();
        string reference;
        await using (var db = NewContext(cs)) reference = (await Svc(db, gw, note).GetOrCreateAsync("Invoice", inv, "INV-1", 23000m, "a@b.example", "PetMart", default))!.Reference;

        gw.Verification = new GatewayVerification(true, 2_300_000, "NGN", "card");
        await using (var db = NewContext(cs))
        {
            var r = await Svc(db, gw, note).SettleAsync(reference, default);
            Assert.True(r.Applied);
        }
        await using var check = NewContext(cs);
        var invoice = await check.Invoices.SingleAsync(i => i.Id == inv);
        Assert.Equal(("Paid", 23000m), (invoice.Status, invoice.AmountPaid));
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
        Assert.Equal("Paystack", (await check.Payments.SingleAsync()).Method);
        var link = await check.PaymentLinks.SingleAsync();
        Assert.Equal((PaymentLinkStatuses.Paid, 23000m, "card"), (link.Status, link.PaidAmount, link.Channel));
        Assert.NotNull(link.NotifiedAt);
        var sent = Assert.Single(note.Sent);
        Assert.Contains("PetMart", sent.Message); Assert.Contains("23,000.00", sent.Message); Assert.Contains("paid", sent.Message);

        // Paystack retries webhooks: a second delivery must not pay the invoice twice or message the admin twice.
        await using (var db = NewContext(cs)) Assert.True((await Svc(db, gw, note).SettleAsync(reference, default)).AlreadySettled);
        await using var again = NewContext(cs);
        Assert.Equal(23000m, (await again.Invoices.SingleAsync(i => i.Id == inv)).AmountPaid);
        Assert.Single(await again.Payments.ToListAsync());
        Assert.Single(note.Sent);
    }

    [Fact]
    public async Task A_payment_the_processor_does_not_confirm_changes_nothing()
    {
        var (cs, _, inv) = await Invoice();
        var gw = new FakeGateway(); var note = new RecordingNotifier();
        string reference;
        await using (var db = NewContext(cs)) reference = (await Svc(db, gw, note).GetOrCreateAsync("Invoice", inv, "INV-1", 23000m, null, "PetMart", default))!.Reference;

        foreach (var v in new GatewayVerification?[] { null, new(false, 2_300_000, "NGN", "card"), new(true, 2_300_000, "USD", "card") })
        {
            gw.Verification = v;
            await using var db = NewContext(cs);
            Assert.False((await Svc(db, gw, note).SettleAsync(reference, default)).Applied);
        }
        await using var check = NewContext(cs);
        Assert.Equal(0m, (await check.Invoices.SingleAsync(i => i.Id == inv)).AmountPaid);
        Assert.Empty(note.Sent);
        Assert.False((await Svc(check, gw, note).SettleAsync("test-I999-unknown", default)).Applied);
    }

    [Fact]
    public async Task An_overpayment_is_applied_up_to_the_balance_and_flagged_for_the_admin()
    {
        var (cs, _, inv) = await Invoice(paid: 10000);                     // 13,000 still owed
        var gw = new FakeGateway(); var note = new RecordingNotifier();
        string reference;
        await using (var db = NewContext(cs)) reference = (await Svc(db, gw, note).GetOrCreateAsync("Invoice", inv, "INV-1", 13000m, null, "PetMart", default))!.Reference;
        // Meanwhile the customer also paid the shop 5,000 in cash, so only 8,000 is owed when the online payment lands.
        await using (var db = NewContext(cs)) await new CustomerPaymentService(db, Wire.Tx(db), new SystemClock()).RecordAsync((await db.Customers.SingleAsync()).Id, 5000, "Cash", Clerk);

        gw.Verification = new GatewayVerification(true, 1_300_000, "NGN", "bank_transfer");
        await using (var db = NewContext(cs)) await Svc(db, gw, note).SettleAsync(reference, default);
        await using var check = NewContext(cs);
        Assert.Equal(23000m, (await check.Invoices.SingleAsync(i => i.Id == inv)).AmountPaid);   // capped at the total
        Assert.Contains("5,000.00 more than was owed", Assert.Single(note.Sent).Message);
    }

    [Fact]
    public async Task A_failed_notification_does_not_undo_the_payment_and_stays_visible()
    {
        var (cs, _, inv) = await Invoice();
        var gw = new FakeGateway(); var note = new RecordingNotifier { Fail = true };
        string reference;
        await using (var db = NewContext(cs)) reference = (await Svc(db, gw, note).GetOrCreateAsync("Invoice", inv, "INV-1", 23000m, null, "PetMart", default))!.Reference;
        gw.Verification = new GatewayVerification(true, 2_300_000, "NGN", "card");
        await using (var db = NewContext(cs)) Assert.True((await Svc(db, gw, note).SettleAsync(reference, default)).Applied);
        await using var check = NewContext(cs);
        Assert.Equal("Paid", (await check.Invoices.SingleAsync(i => i.Id == inv)).Status);
        Assert.Null((await check.PaymentLinks.SingleAsync()).NotifiedAt);
    }

    [Fact]
    public async Task A_paid_quotation_is_recorded_and_announced_without_touching_any_ledger()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 5);
        var gw = new FakeGateway(); var note = new RecordingNotifier();
        string reference;
        await using (var db = NewContext(cs)) reference = (await Svc(db, gw, note).GetOrCreateAsync("Quotation", 7, "Q-7", 27190.5m, null, "PetMart", default))!.Reference;
        gw.Verification = new GatewayVerification(true, 2_719_050, "NGN", "ussd");
        await using (var db = NewContext(cs)) await Svc(db, gw, note).SettleAsync(reference, default);
        await using var check = NewContext(cs);
        Assert.Equal(0m, (await check.Customers.SingleAsync()).Balance);
        Assert.Empty(await check.Payments.ToListAsync());
        Assert.Contains("Quotation Q-7", Assert.Single(note.Sent).Message);
    }

    [Fact]
    public void Webhook_signatures_are_HMAC_SHA512_of_the_raw_body_and_forgeries_are_refused()
    {
        var gw = new PaystackGateway(new HttpClient(), Options.Create(new PaystackOptions { SecretKey = "sk_test_abc123" }), Options.Create(new NotifyOptions()), NullLogger<PaystackGateway>.Instance);
        const string body = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"test-I1-1\"}}";
        var good = Convert.ToHexString(System.Security.Cryptography.HMACSHA512.HashData("sk_test_abc123"u8.ToArray(), System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.True(gw.IsValidSignature(body, good));
        Assert.True(gw.IsValidSignature(body, good.ToUpperInvariant()));
        Assert.False(gw.IsValidSignature(body + " ", good));               // body tampered with
        Assert.False(gw.IsValidSignature(body, new string('0', 128)));
        Assert.False(gw.IsValidSignature(body, null));
        Assert.False(new PaystackGateway(new HttpClient(), Options.Create(new PaystackOptions()), Options.Create(new NotifyOptions()), NullLogger<PaystackGateway>.Instance).IsValidSignature(body, good));   // no key configured
    }

    [Theory]
    [InlineData("09150464707", "2349150464707")]
    [InlineData("+234 915 046 4707", "2349150464707")]
    [InlineData("2349150464707", "2349150464707")]
    public void Nigerian_numbers_become_international_form_for_sms(string given, string expected) => Assert.Equal(expected, AdminNotifier.International(given));
}
