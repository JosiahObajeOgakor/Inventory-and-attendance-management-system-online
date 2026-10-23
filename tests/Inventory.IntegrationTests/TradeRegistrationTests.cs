using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Application.Partners;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class CapturingEmail : IBrandedEmail
{
    public bool IsConfigured => true;
    public List<BrandedMail> Sent { get; } = [];
    public Task SendAsync(BrandedMail message, CancellationToken ct) { Sent.Add(message); return Task.CompletedTask; }
    /// <summary>The code from the latest code email.</summary>
    public string LastCode => Sent.Last(m => m.Code is not null).Code!;
}

internal sealed class AdjustableClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
    public DateOnly BusinessToday => DateOnly.FromDateTime(UtcNow.AddHours(1));
    public DateTime BusinessNow => UtcNow.AddHours(1);
}

/// <summary>One store, so "every chosen store" is this one; its services come from a small container over the test database.</summary>
internal sealed class OneStore(IServiceProvider sp) : ICompanyScopes
{
    public IReadOnlyList<string> TradeStores => ["chewypets", "candid"];
    public string DisplayName(string key) => key;
    public Task<T> InAsync<T>(string key, Func<IServiceProvider, Task<T>> work) => key == "chewypets" ? work(sp) : Task.FromResult(default(T)!);
}

/// <summary>The website's step-by-step trade registration: confirmed email, a saved step at a time, resume on the same or another device, follow-ups and clean-up.</summary>
[Collection("mysql")]
public class TradeRegistrationTests(MySqlFixture mysql)
{
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A];

    private sealed record Rig(TradeRegistrationService Svc, BusinessDbContext Db, CapturingEmail Mail, AdjustableClock Clock, RecordingStaffAlerts Alerts);

    private async Task<Rig> BuildAsync()
    {
        var cs = await mysql.NewSchemaAsync();
        await SeedAsync(cs, 5);
        var db = NewContext(cs);
        var clock = new AdjustableClock(); var mail = new CapturingEmail(); var alerts = new RecordingStaffAlerts();
        var co = new CompanyContext(new CompanyInfo("chewypets", "Chewy Pet", "ChewyStock", "x", "Chewy Pet Ltd"));
        var services = new ServiceCollection()
            .AddSingleton<IBusinessDbContext>(db).AddSingleton<IClock>(clock).AddSingleton<ICompanyContext>(co).AddSingleton<IBusinessVerifier>(new FakeVerifier(configured: false))
            .AddSingleton<IStaffAlerts>(alerts).AddLogging().AddTransient<TradePartnerService>().BuildServiceProvider();
        return new Rig(new TradeRegistrationService(db, clock, mail, new OneStore(services), NullLogger<TradeRegistrationService>.Instance), db, mail, clock, alerts);
    }

    private static JsonObject J(params (string K, string V)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }

    private static async Task<string> ToReviewAsync(Rig r, string email = "bola@example.com")
    {
        var start = await r.Svc.StartAsync(email, null, default);
        var v = await r.Svc.VerifyAsync(email, r.Mail.LastCode, start.DeviceToken, default);
        var token = v.DeviceToken ?? start.DeviceToken!;
        await r.Svc.SaveStepAsync(token, 2, J(("businessName", "Paws Pets"), ("tier", "Wholesaler"), ("stores", "chewypets"), ("description", "Two pet shops in Ikeja, 80 bags a month.")), default);
        await r.Svc.SaveStepAsync(token, 3, J(("contactName", "Bola Ade"), ("phone", "0803 123 4567"), ("address", "12 Allen Avenue, Ikeja")), default);
        await r.Svc.SaveStepAsync(token, 4, J(("cacNumber", "RC1234567"), ("taxId", "12345678-0001")), default);
        await r.Svc.UploadAsync(token, "cac", "cert.pdf", "application/pdf", Pdf, default);
        await r.Svc.DocumentsDoneAsync(token, default);
        return token;
    }

    [Fact]
    public async Task An_application_goes_step_by_step_after_the_email_is_confirmed_and_is_sent_to_the_store()
    {
        var r = await BuildAsync();
        var start = await r.Svc.StartAsync("Bola@Example.com", null, default);
        Assert.Equal("code", start.Next);
        Assert.NotNull(start.DeviceToken);
        // nothing can be saved before the email is confirmed
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Svc.SaveStepAsync(start.DeviceToken, 2, J(("businessName", "Paws Pets")), default));
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Svc.VerifyAsync("bola@example.com", "000000" == r.Mail.LastCode ? "111111" : "000000", start.DeviceToken, default));

        var v = await r.Svc.VerifyAsync("bola@example.com", r.Mail.LastCode, start.DeviceToken, default);
        Assert.False(v.Partner);
        Assert.Equal(2, v.State!.Step);
        // a step can't be skipped, and each step is checked with the same rules as the final application
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Svc.SaveStepAsync(start.DeviceToken, 4, J(("cacNumber", "RC1234567"), ("taxId", "12345678-0001")), default));
        await Assert.ThrowsAsync<BusinessRuleException>(() => r.Svc.SaveStepAsync(start.DeviceToken, 2, J(("businessName", "P"), ("tier", "Wholesaler"), ("stores", "both"), ("description", "short")), default));

        r.Db.ChangeTracker.Clear();
        var token = await ToReviewAsync(r, "second@example.com");
        var sent = await r.Svc.SubmitAsync(token, default);
        Assert.Contains("with our managers", sent.Message);
        r.Db.ChangeTracker.Clear();
        var p = await r.Db.TradePartners.Include(x => x.Documents).SingleAsync(x => x.Email == "second@example.com");
        Assert.Equal("2348031234567", p.Phone);
        Assert.Equal(PartnerStatuses.Pending, p.Status);
        Assert.Single(p.Documents);
        Assert.False(await r.Db.TradeApplicationDrafts.AnyAsync(d => d.Email == "second@example.com"));
        Assert.Contains(r.Alerts.Staff, a => a.Text.Contains("TRADE APPLICATION"));
    }

    [Fact]
    public async Task The_same_device_carries_on_where_it_stopped_and_another_device_carries_on_after_a_code()
    {
        var r = await BuildAsync();
        var start = await r.Svc.StartAsync("bola@example.com", null, default);
        await r.Svc.VerifyAsync("bola@example.com", r.Mail.LastCode, start.DeviceToken, default);
        await r.Svc.SaveStepAsync(start.DeviceToken, 2, J(("businessName", "Paws Pets"), ("tier", "Distributor"), ("stores", "both"), ("description", "Two pet shops in Ikeja, 80 bags a month.")), default);

        var same = await r.Svc.ResumeAsync(start.DeviceToken, default);
        Assert.Equal(3, same!.Step);
        Assert.Equal("Paws Pets", same.Data["businessName"]);
        var again = await r.Svc.StartAsync("bola@example.com", start.DeviceToken, default);
        Assert.Equal("resume", again.Next);

        r.Mail.Sent.Clear(); r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(2);
        var other = await r.Svc.StartAsync("bola@example.com", null, default);
        Assert.Equal("code", other.Next);
        Assert.Null(other.DeviceToken);                                                  // nothing is handed out before the code
        var v = await r.Svc.VerifyAsync("bola@example.com", r.Mail.LastCode, null, default);
        Assert.NotNull(v.DeviceToken);
        Assert.Equal(3, v.State!.Step);
        Assert.Null(await r.Svc.ResumeAsync(start.DeviceToken, default));                 // the old device no longer opens it
    }

    [Fact]
    public async Task A_quiet_application_is_followed_up_at_3_7_and_21_days_then_removed_24_hours_after_the_warning()
    {
        var r = await BuildAsync();
        var start = await r.Svc.StartAsync("quiet@example.com", null, default);
        await r.Svc.VerifyAsync("quiet@example.com", r.Mail.LastCode, start.DeviceToken, default);
        await r.Svc.SaveStepAsync(start.DeviceToken, 2, J(("businessName", "Quiet Pets"), ("tier", "Wholesaler"), ("stores", "chewypets"), ("description", "A pet shop in Yaba selling dog food.")), default);
        var began = r.Clock.UtcNow;
        r.Mail.Sent.Clear();

        async Task<(int, int)> At(double days) { r.Clock.UtcNow = began.AddDays(days); r.Db.ChangeTracker.Clear(); return await r.Svc.FollowUpAsync("https://chewypetsfeeds.com", default); }
        Assert.Equal((0, 0), await At(2));
        Assert.Equal((1, 0), await At(3.1));
        Assert.Equal((0, 0), await At(5));
        Assert.Equal((1, 0), await At(7.1));
        Assert.Equal((1, 0), await At(21.1));
        Assert.Contains("24 hours", r.Mail.Sent.Last().Subject);
        Assert.Contains("Quiet Pets", r.Mail.Sent[0].Subject);
        Assert.All(r.Mail.Sent, m => Assert.Equal("https://chewypetsfeeds.com/#trade", m.ButtonUrl));
        Assert.All(r.Mail.Sent, m => Assert.Equal(["chewypets"], m.Stores));                         // this applicant chose Chewy Pet: its logo heads the emails
        Assert.Equal((0, 0), await At(21.5));                                              // inside the 24 hours: kept
        Assert.Equal((0, 1), await At(22.2));                                              // then removed
        Assert.False(await r.Db.TradeApplicationDrafts.AnyAsync());

        r.Clock.UtcNow = began.AddDays(23);
        Assert.Equal("code", (await r.Svc.StartAsync("quiet@example.com", null, default)).Next);   // the address is free to start again
    }

    [Fact]
    public async Task Activity_after_a_follow_up_starts_the_clock_again()
    {
        var r = await BuildAsync();
        var start = await r.Svc.StartAsync("busy@example.com", null, default);
        await r.Svc.VerifyAsync("busy@example.com", r.Mail.LastCode, start.DeviceToken, default);
        var began = r.Clock.UtcNow;
        r.Clock.UtcNow = began.AddDays(22); r.Db.ChangeTracker.Clear();
        await r.Svc.FollowUpAsync("https://x.test", default);                                      // the 3-day note (sequential, never straight to the warning)
        Assert.DoesNotContain("24 hours", r.Mail.Sent.Last().Subject);
        await r.Svc.SaveStepAsync(start.DeviceToken, 2, J(("businessName", "Busy Pets"), ("tier", "Wholesaler"), ("stores", "chewypets"), ("description", "A pet shop in Yaba selling dog food.")), default);
        var d = await r.Db.TradeApplicationDrafts.AsNoTracking().SingleAsync();
        Assert.Equal(0, d.RemindersSent);
        Assert.Null(d.WarnedAt);
    }

    [Fact]
    public async Task A_browser_trusted_after_a_code_signs_in_without_one_and_any_other_browser_needs_a_code()
    {
        var r = await BuildAsync();
        var token = await ToReviewAsync(r);
        await r.Svc.SubmitAsync(token, default);
        var trusted = await r.Svc.TrustAsync("bola@example.com", "102.89.1.1", "Edge", default);
        Assert.True(await r.Svc.IsTrustedAsync("Bola@Example.com", trusted, "41.58.2.2", default));     // same browser, even from another network
        Assert.False(await r.Svc.IsTrustedAsync("bola@example.com", null, "102.89.1.1", default));        // another browser has no token
        Assert.False(await r.Svc.IsTrustedAsync("someone@example.com", trusted, "102.89.1.1", default));  // the token belongs to one email only
        await r.Svc.ForgetAsync(trusted, default);
        Assert.False(await r.Svc.IsTrustedAsync("bola@example.com", trusted, "102.89.1.1", default));     // signed out: a code next time
    }

    [Fact]
    public async Task An_email_that_already_belongs_to_a_partner_signs_them_in()
    {
        var r = await BuildAsync();
        var token = await ToReviewAsync(r);
        await r.Svc.SubmitAsync(token, default);
        r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(5);
        var start = await r.Svc.StartAsync("bola@example.com", null, default);
        Assert.Equal("login", start.Next);
        var v = await r.Svc.VerifyAsync("bola@example.com", r.Mail.LastCode, null, default);
        Assert.True(v.Partner);
    }
}
