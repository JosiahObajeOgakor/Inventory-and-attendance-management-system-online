using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Partners;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class FakeVerifier(VerificationResult? result = null, bool configured = true) : IBusinessVerifier
{
    public bool IsConfigured => configured;
    public int Calls { get; private set; }
    public Task<VerificationResult> VerifyAsync(string cacNumber, string businessName, CancellationToken ct)
    { Calls++; return Task.FromResult(result ?? new VerificationResult(PartnerVerifications.Verified, businessName.ToUpperInvariant(), null)); }
}

internal sealed class RecordingStaffAlerts : IStaffAlerts
{
    public List<StaffAlert> Staff { get; } = [];
    public List<ContactAlert> Contacts { get; } = [];
    public Task TellStaffAsync(StaffAlert alert, CancellationToken ct) { Staff.Add(alert); return Task.CompletedTask; }
    public Task TellContactAsync(ContactAlert alert, CancellationToken ct) { Contacts.Add(alert); return Task.CompletedTask; }
}

/// <summary>Applying for trade prices, and staff approving, rejecting, suspending and deleting: on a real MySQL.</summary>
[Collection("mysql")]
public class TradePartnerTests(MySqlFixture mysql)
{
    private static readonly CurrentUser Manager = new(1, "Ada Manager", "Manager");
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A];

    private static PartnerApplication App(string phone = "0803 123 4567", string tier = "Wholesaler") =>
        new("Paws & Claws Pet Supplies", "Bola Ade", phone, "Bola@Example.com", "12 Allen Avenue, Ikeja, Lagos", "rc 1234567", "12345678-0001",
            "We run two pet shops in Ikeja and sell about 80 bags of dog food a month.", tier);

    private async Task<(TradePartnerService Svc, Inventory.Infrastructure.Persistence.BusinessDbContext Db, RecordingStaffAlerts Alerts, FakeVerifier Verifier)> BuildAsync(FakeVerifier? verifier = null)
    {
        var cs = await mysql.NewSchemaAsync();
        await SeedAsync(cs, 5);
        var db = NewContext(cs);
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd"));
        var alerts = new RecordingStaffAlerts(); var v = verifier ?? new FakeVerifier();
        return (new TradePartnerService(db, new SystemClock(), co, v, alerts, NullLogger<TradePartnerService>.Instance), db, alerts, v);
    }

    [Fact]
    public async Task An_application_is_pending_checked_and_the_admin_and_applicant_are_told()
    {
        var (svc, db, alerts, verifier) = await BuildAsync();
        var r = await svc.ApplyAsync(App(), [new("cac", "cert.pdf", "application/pdf", Pdf)], "website", default);

        Assert.Equal(PartnerStatuses.Pending, r.Status);
        var p = await db.TradePartners.Include(x => x.Documents).SingleAsync();
        Assert.Equal("2348031234567", p.Phone);
        Assert.Equal("RC1234567", p.CacNumber);
        Assert.Equal("bola@example.com", p.Email);
        Assert.Equal(PartnerVerifications.Verified, p.Verification);
        Assert.Single(p.Documents);
        Assert.Equal(1, verifier.Calls);
        Assert.Contains("TRADE APPLICATION", Assert.Single(alerts.Staff).Text);
        Assert.Equal("2348031234567", Assert.Single(alerts.Contacts).Phone);
        Assert.Null(p.CustomerId);                                                        // no trade prices until a manager decides
    }

    [Fact]
    public async Task A_website_application_needs_the_CAC_certificate_and_a_fake_file_is_refused()
    {
        var (svc, db, _, _) = await BuildAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ApplyAsync(App(), [], "website", default));
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ApplyAsync(App(), [new("cac", "cert.pdf", "application/pdf", [0x4D, 0x5A, 0x90, 0x00, 0x03])], "website", default));
        Assert.Equal(0, await db.TradePartners.CountAsync());
    }

    [Fact]
    public async Task Applying_again_from_the_same_number_updates_the_application_instead_of_duplicating_it()
    {
        var (svc, db, alerts, _) = await BuildAsync();
        await svc.ApplyAsync(App(), [new("cac", "a.pdf", "application/pdf", Pdf)], "website", default);
        await svc.ApplyAsync(App("+234 803 123 4567", "Distributor"), [new("cac", "b.pdf", "application/pdf", Pdf)], "website", default);
        var p = await db.TradePartners.Include(x => x.Documents).SingleAsync();
        Assert.Equal("Distributor", p.RequestedTier);
        Assert.Equal("b.pdf", Assert.Single(p.Documents).FileName);
        Assert.Contains("UPDATED", alerts.Staff[1].Text);
    }

    [Fact]
    public async Task Approval_books_the_partner_to_a_customer_at_the_chosen_tier_and_suspension_takes_it_back()
    {
        var (svc, db, alerts, _) = await BuildAsync();
        var id = (await svc.ApplyAsync(App(), [new("cac", "a.pdf", "application/pdf", Pdf)], "website", default)).Id;

        await svc.ApproveAsync(id, "Distributor", "Good volumes", Manager, default);
        db.ChangeTracker.Clear();
        var p = await db.TradePartners.Include(x => x.Customer).SingleAsync();
        Assert.Equal(PartnerStatuses.Approved, p.Status);
        Assert.Equal("Distributor", p.ApprovedTier);                                     // staff may grant a different tier from the one asked for
        Assert.Equal("Distributor", p.Customer!.CustomerType);
        Assert.Equal("Paws & Claws Pet Supplies", p.Customer.Name);
        Assert.Contains(alerts.Contacts, c => c.Subject.Contains("approved"));
        Assert.Contains(await db.AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "PARTNER_APPROVED");

        var again = await svc.ApplyAsync(App(), [], "whatsapp", default);
        Assert.Contains("already approved", again.Message);

        await svc.SuspendAsync(id, true, "Unpaid balance", Manager, default);
        db.ChangeTracker.Clear();
        p = await db.TradePartners.Include(x => x.Customer).SingleAsync();
        Assert.Equal(PartnerStatuses.Suspended, p.Status);
        Assert.Equal("Retailer", p.Customer!.CustomerType);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ApplyAsync(App(), [], "whatsapp", default));

        await svc.SuspendAsync(id, false, null, Manager, default);
        db.ChangeTracker.Clear();
        Assert.Equal("Distributor", (await db.Customers.SingleAsync(c => c.Id == p.CustomerId)).CustomerType);
    }

    [Fact]
    public async Task A_partner_with_wallet_records_cannot_be_deleted_only_suspended()
    {
        var (svc, db, _, _) = await BuildAsync();
        var id = (await svc.ApplyAsync(App(), [new("cac", "a.pdf", "application/pdf", Pdf)], "website", default)).Id;
        db.WalletEntries.Add(new WalletEntry { TradePartnerId = id, Amount = 5000, Reference = "W-1", Method = "transfer", Status = WalletEntryStatuses.Confirmed, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DeleteAsync(id, Manager, default));

        var other = (await svc.ApplyAsync(App("08099998888"), [new("cac", "a.pdf", "application/pdf", Pdf)], "website", default)).Id;
        await svc.DeleteAsync(other, Manager, default);
        Assert.Equal(1, await db.TradePartners.CountAsync());
        Assert.Equal(0, await db.TradePartnerDocuments.CountAsync(d => d.TradePartnerId == other));
        Assert.Equal(5000, (await svc.ListAsync(null, default)).Single().WalletBalance);
    }

    [Fact]
    public async Task Without_a_verification_provider_the_application_is_marked_for_a_manual_check()
    {
        var (svc, db, alerts, verifier) = await BuildAsync(new FakeVerifier(configured: false));
        await svc.ApplyAsync(App(), [new("cac", "a.pdf", "application/pdf", Pdf)], "website", default);
        Assert.Equal(PartnerVerifications.NotChecked, (await db.TradePartners.SingleAsync()).Verification);
        Assert.Equal(0, verifier.Calls);
        Assert.Contains("look at the certificate", alerts.Staff.Single().Text);
    }
}
