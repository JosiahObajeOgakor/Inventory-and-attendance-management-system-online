using Inventory.Application.Analytics;
using Inventory.Application.Abstractions;
using Inventory.Application.Campaigns;
using Inventory.Application.Company;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Application.Trade;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class FakeEmail : ICampaignEmailSender
{
    public bool IsConfigured => true;
    public List<(string To, string Subject, string Text, string Unsub)> Sent { get; } = [];
    public Task<bool> TrySendAsync(string toEmail, string? toName, string fromName, string subject, string html, string text, string unsubscribeUrl, CancellationToken ct)
    { Sent.Add((toEmail, subject, text, unsubscribeUrl)); return Task.FromResult(true); }
}

/// <summary>09:00 Lagos on today's date, so the quiet-hours rule never depends on when the tests happen to run.</summary>
internal sealed class MorningClock : IClock
{
    private static readonly DateOnly Today = new SystemClock().BusinessToday;
    public DateTime UtcNow => Today.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
    public DateOnly BusinessToday => Today;
    public DateTime BusinessNow => UtcNow.AddHours(1);
}

[Collection("mysql")]
public class CampaignTests(MySqlFixture mysql)
{
    private const string Ngozi = "2348033333333";
    private static readonly MorningClock Clock = new();

    private sealed class Rig
    {
        public required string Cs; public required BusinessDbContext Db; public required Seed Seed; public required CampaignService Campaigns; public required CampaignRunner Runner;
        public required ChatFlowService Flow; public required PaymentLinkService Pay; public required FakeVirtualAccounts Va; public required CountingModel Ai;
        public required RecordingSender Sender; public required FakeEmail Email; public required RecordingNotifier Admin;
        public async Task<string> Say(string phone, string text, string name = "Ngozi Eze") =>
            string.Join("\n", (await Flow.HandleAsync(ChatChannels.WhatsApp, phone, name, text, default)).Messages);
        public async Task<Campaign> Campaign(string type) { await Campaigns.EnsureDefaultsAsync(default); Db.ChangeTracker.Clear(); return await Db.Campaigns.AsNoTracking().SingleAsync(c => c.Type == type); }
    }

    private async Task<Rig> BuildAsync(int stock, string aiJson = "{\"intent\":\"other\",\"items\":[]}", Action<CampaignOptions>? configure = null)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock);
        var db = NewContext(cs);
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", DefaultWarehouseId: seed.WarehouseId));
        var tx = Wire.Tx(db);
        var quotes = new QuotationService(db, tx, Clock, co, SalesFor(db), new NoUsers());
        var admin = new RecordingNotifier(); var va = new FakeVirtualAccounts(); var ai = new CountingModel(aiJson); var sender = new RecordingSender(); var email = new FakeEmail();
        var pay = new PaymentLinkService(db, tx, Clock, co, new FakeGateway(false), admin, new CustomerPaymentService(db, tx, Clock), quotes, va);
        var chatOpts = Options.Create(new SalesAssistantOptions());
        var campOpts = new CampaignOptions { AdminName = "Ada Obi", AdminPhone = "09150464707", AdminEmail = "admin@example.com", PublicBaseUrl = "https://shop.example", SecretKey = "test-secret" };
        configure?.Invoke(campOpts);
        var stockSvc = new StockAvailabilityService(db, co, Clock, chatOpts);
        var texts = new ChatOrderText(db, co);
        var consent = new ConsentService(db, Clock);
        var flow = new ChatFlowService(db, new ChatConversationService(db, Clock), new CustomerLookupService(db), quotes, pay, stockSvc,
            new ChatAiInterpreter(ai, db, chatOpts, NullLogger<ChatAiInterpreter>.Instance), texts, new CompanyProfileService(db, co, Clock), co, Clock, chatOpts, NullLogger<ChatFlowService>.Instance, consent);
        var planner = new CampaignPlanner(db, Clock, stockSvc, new AnalyticsService(db, Clock), co);
        var writer = new CampaignWriter(ai, Options.Create(campOpts), NullLogger<CampaignWriter>.Instance);
        var runner = new CampaignRunner(db, planner, writer, sender, email, Clock, co, Options.Create(campOpts), NullLogger<CampaignRunner>.Instance);
        return new Rig { Cs = cs, Db = db, Seed = seed, Campaigns = new CampaignService(db, runner, Clock, NullLogger<CampaignService>.Instance), Runner = runner, Flow = flow, Pay = pay, Va = va, Ai = ai, Sender = sender, Email = email, Admin = admin };
    }

    /// <summary>A customer who bought <paramref name="qty"/> bags <paramref name="daysAgo"/> days ago, so the sale is on the books exactly as the till would have recorded it.</summary>
    private static async Task<int> CustomerBoughtAsync(Rig r, string name, string phone, string status, int daysAgo, int qty, string? email = null, string? lang = null)
    {
        await using var db = NewContext(r.Cs);
        var c = new Customer { Name = name, Phone = phone, Email = email, CustomerType = "Retailer", MarketingStatus = status, PreferredLanguage = lang };
        db.Customers.Add(c); await db.SaveChangesAsync();
        var req = Sale(r.Seed, qty, vat: 0, paid: qty * 11500m);
        req.CustomerId = c.Id; req.SaleDate = Clock.BusinessToday.AddDays(-daysAgo); req.PaymentMethod = "Cash";
        await SalesFor(db).SaveAsync(req, Clerk, null);
        return c.Id;
    }

    private static async Task SetStockAsync(Rig r, int qty) { await r.Db.StockBatches.ExecuteUpdateAsync(u => u.SetProperty(b => b.QuantityOnHand, qty)); r.Db.ChangeTracker.Clear(); }

    // ------------------------------------------------------------------ opt-in inside the sales chat

    [Fact]
    public async Task A_first_time_customer_is_asked_once_for_permission_before_the_order_summary_and_the_answer_is_stored()
    {
        var r = await BuildAsync(stock: 10);
        var ask = await r.Say(Ngozi, "abeg i wan buy 2 adult dog food");
        Assert.Contains("(yes / no)", ask);
        Assert.Contains("STOP", ask);
        Assert.DoesNotContain("Your order:", ask);                         // permission comes first

        var next = await r.Say(Ngozi, "yes");
        Assert.Contains("Your order:", next);
        await r.Say(Ngozi, "yes");                                         // confirm the order -> customer record is created with the consent
        r.Db.ChangeTracker.Clear();
        var c = await r.Db.Customers.AsNoTracking().SingleAsync(x => x.Phone == Ngozi);
        Assert.Equal(MarketingStatuses.OptedIn, c.MarketingStatus);
        Assert.Equal("chat", c.MarketingSource);
        Assert.Equal(ChatLanguages.Pidgin, c.PreferredLanguage);

        // a later order from the same customer does not ask again
        await r.Say(Ngozi, "cancel");
        var again = await r.Say(Ngozi, "i need 1 adult dog food");
        Assert.Contains("Your order:", again);
        Assert.DoesNotContain("reminder", again, StringComparison.OrdinalIgnoreCase);         // no second permission question
    }

    [Fact]
    public async Task Saying_no_is_respected_and_the_sale_still_goes_ahead()
    {
        var r = await BuildAsync(stock: 10);
        await r.Say(Ngozi, "hello i need 1 adult dog food");
        var summary = await r.Say(Ngozi, "no");
        Assert.Contains("Your order:", summary);
        await r.Say(Ngozi, "yes");
        r.Db.ChangeTracker.Clear();
        Assert.Equal(MarketingStatuses.OptedOut, (await r.Db.Customers.AsNoTracking().SingleAsync(x => x.Phone == Ngozi)).MarketingStatus);
        Assert.Equal(1, r.Va.Created);                                     // opting out of marketing never blocks buying
    }

    [Fact]
    public async Task Stop_and_subscribe_work_at_any_time_and_a_bare_stop_in_an_order_still_cancels_the_order()
    {
        var r = await BuildAsync(stock: 10);
        var id = await CustomerBoughtAsync(r, "Ngozi Eze", Ngozi, MarketingStatuses.OptedIn, 20, 1);

        Assert.Contains("won't get reminders", await r.Say(Ngozi, "STOP"), StringComparison.OrdinalIgnoreCase);
        r.Db.ChangeTracker.Clear();
        Assert.Equal(MarketingStatuses.OptedOut, (await r.Db.Customers.AsNoTracking().SingleAsync(c => c.Id == id)).MarketingStatus);

        Assert.Contains("subscribed again", await r.Say(Ngozi, "subscribe"));
        r.Db.ChangeTracker.Clear();
        Assert.Equal(MarketingStatuses.OptedIn, (await r.Db.Customers.AsNoTracking().SingleAsync(c => c.Id == id)).MarketingStatus);

        await r.Say(Ngozi, "2 adult dog food");                           // order in progress (summary shown)
        Assert.Contains("cancelled", await r.Say(Ngozi, "stop"), StringComparison.OrdinalIgnoreCase);
        r.Db.ChangeTracker.Clear();
        Assert.Equal(MarketingStatuses.OptedIn, (await r.Db.Customers.AsNoTracking().SingleAsync(c => c.Id == id)).MarketingStatus);   // that STOP was about the order
    }

    // ------------------------------------------------------------------ the reorder reminder, end to end

    [Fact]
    public async Task A_reminder_is_built_from_the_customers_own_history_and_todays_stock_and_a_yes_reply_completes_the_sale()
    {
        var r = await BuildAsync(stock: 10);
        await r.Campaigns.EnsureDefaultsAsync(default);
        var ngozi = await CustomerBoughtAsync(r, "Ngozi Eze", Ngozi, MarketingStatuses.OptedIn, daysAgo: 24, qty: 2, lang: ChatLanguages.Pidgin);
        await CustomerBoughtAsync(r, "Musa Bello", "2348044444444", MarketingStatuses.OptedOut, 24, 1);           // said no
        await CustomerBoughtAsync(r, "Ife Ade", "2348055555555", MarketingStatuses.Unknown, 24, 1);               // never said yes
        await CustomerBoughtAsync(r, "Tunde Ola", "2348066666666", MarketingStatuses.OptedIn, 2, 1);              // bought two days ago: not due
        await SetStockAsync(r, 3);                                           // small shelf now: the shop is about to run out
        await r.Say(Ngozi, "hello");                                         // she has written to us before, so the 24-hour window is open and free text is allowed

        var camp = await r.Campaign(CampaignTypes.Reorder);
        var result = await r.Runner.RunAsync(camp, preview: false, default);

        Assert.Equal(1, result.Sent);
        var sent = Assert.Single(r.Sender.Sent);
        Assert.Equal(Ngozi, sent.To);
        Assert.Contains("Ngozi", sent.Text);                                // her first name
        Assert.Contains("Adult Dog Food 20kg", sent.Text);                  // the product she actually bought
        Assert.Contains("3", sent.Text);                                    // today's stock is in the message because the shelf is low (urgent wording)
        Assert.Contains("YES", sent.Text);                                  // the call to action
        Assert.Contains("STOP", sent.Text);                                 // and the way out

        // she replies YES: the chat knows what that means, takes her order for the same 2 bags, and shows the bank account
        var reply = await r.Say(Ngozi, "yes");
        Assert.Contains("8880007001", reply);
        r.Db.ChangeTracker.Clear();
        var quote = await r.Db.Quotations.AsNoTracking().Include(q => q.Items).SingleAsync();
        Assert.Equal(ngozi, quote.CustomerId);
        Assert.Equal(2, quote.Items.Single().Quantity);

        // and when she pays, the sale completes and she is told in the chat
        var link = await r.Db.PaymentLinks.AsNoTracking().SingleAsync();
        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, link.Amount, "completed");
        var settled = await r.Pay.SettleAsync(link.Reference, default);
        Assert.True(settled.Applied);
    }

    [Fact]
    public async Task Nobody_is_messaged_twice_inside_the_cooldown_and_a_product_we_cannot_sell_is_never_pitched()
    {
        var r = await BuildAsync(stock: 8);
        await CustomerBoughtAsync(r, "Ngozi Eze", Ngozi, MarketingStatuses.OptedIn, 25, 1);
        await r.Say(Ngozi, "hello");
        var camp = await r.Campaign(CampaignTypes.Reorder);

        Assert.Equal(1, (await r.Runner.RunAsync(camp, false, default)).Sent);
        Assert.Equal(0, (await r.Runner.RunAsync(camp, false, default)).Sent);            // second run the same morning: cooldown

        var r2 = await BuildAsync(stock: 8);
        await CustomerBoughtAsync(r2, "Ngozi Eze", Ngozi, MarketingStatuses.OptedIn, 25, 1);
        await r2.Say(Ngozi, "hello");
        await SetStockAsync(r2, 0);                                                      // sold out: reminding her would be a lie
        Assert.Equal(0, (await r2.Runner.RunAsync(await r2.Campaign(CampaignTypes.Reorder), false, default)).Sent);
        Assert.Empty(r2.Sender.Sent);
    }

    [Fact]
    public async Task Outside_the_24_hour_window_whatsapp_needs_an_approved_template_and_the_text_travels_in_its_variables()
    {
        var r = await BuildAsync(stock: 8);
        await CustomerBoughtAsync(r, "Ngozi Eze", Ngozi, MarketingStatuses.OptedIn, 25, 1);
        var camp = await r.Campaign(CampaignTypes.Reorder);

        // The customer has never written to us, so the window is closed.
        var blocked = await r.Runner.RunAsync(camp, false, default);
        Assert.Equal(0, blocked.Sent); Assert.Equal(1, blocked.Skipped);
        Assert.Empty(r.Sender.Sent);
        r.Db.ChangeTracker.Clear();
        Assert.Contains("template", (await r.Db.CampaignMessages.AsNoTracking().SingleAsync()).Detail);
        await r.Db.CampaignMessages.ExecuteDeleteAsync();

        r.Sender.Templates.Add(CampaignTypes.Reorder);
        var sent = await r.Runner.RunAsync(camp, false, default);
        Assert.Equal(1, sent.Sent);
        var t = Assert.Single(r.Sender.TemplatesSent);
        Assert.Equal("Ngozi", t.Vars["1"]);
        Assert.DoesNotContain('\n', t.Vars["2"]);                          // Meta rejects line breaks in template variables
        Assert.Contains("Adult Dog Food 20kg", t.Vars["2"]);
    }

    // ------------------------------------------------------------------ unpaid orders and the admin's restock digest

    [Fact]
    public async Task An_order_left_unpaid_is_nudged_only_while_the_stock_can_still_fill_it_and_yes_gets_a_fresh_account()
    {
        var r = await BuildAsync(stock: 6);
        await r.Say(Ngozi, "abeg i wan buy 2 adult dog food");
        await r.Say(Ngozi, "yes");                                          // consent
        await r.Say(Ngozi, "yes");                                          // order -> account issued, then never paid
        r.Db.ChangeTracker.Clear();
        await r.Db.Quotations.ExecuteUpdateAsync(u => u.SetProperty(q => q.CreatedAt, Clock.UtcNow.AddDays(-2)));
        await r.Db.PaymentLinks.ExecuteUpdateAsync(u => u.SetProperty(l => l.ExpiresAt, Clock.UtcNow.AddDays(-1)));
        await r.Say(Ngozi, "cancel");
        r.Sender.Sent.Clear();

        var camp = await r.Campaign(CampaignTypes.UnpaidOrder);
        var result = await r.Runner.RunAsync(camp, false, default);
        Assert.Equal(1, result.Sent);
        Assert.Contains("Adult Dog Food 20kg", Assert.Single(r.Sender.Sent).Text);

        Assert.Contains("8880007002", await r.Say(Ngozi, "yes"));            // a second account, for the same order

        var r2 = await BuildAsync(stock: 6);
        await r2.Say(Ngozi, "i need 5 adult dog food"); await r2.Say(Ngozi, "yes"); await r2.Say(Ngozi, "yes");
        r2.Db.ChangeTracker.Clear();
        await r2.Db.Quotations.ExecuteUpdateAsync(u => u.SetProperty(q => q.CreatedAt, Clock.UtcNow.AddDays(-2)));
        await r2.Db.PaymentLinks.ExecuteUpdateAsync(u => u.SetProperty(l => l.ExpiresAt, Clock.UtcNow.AddDays(-1)));
        await SetStockAsync(r2, 2);                                          // the stock has gone since: don't tempt her
        Assert.Equal(0, (await r2.Runner.RunAsync(await r2.Campaign(CampaignTypes.UnpaidOrder), false, default)).Sent);
    }

    [Fact]
    public async Task The_admin_gets_a_personal_restock_digest_from_todays_inventory_only_on_days_something_is_at_risk()
    {
        var r = await BuildAsync(stock: 40);
        for (var i = 0; i < 8; i++) await CustomerBoughtAsync(r, $"Buyer {i}", $"23480900000{i:00}", MarketingStatuses.Unknown, daysAgo: 4 + i * 3, qty: 3);
        var camp = await r.Campaign(CampaignTypes.AdminRestock);

        var calm = await r.Runner.RunAsync(camp, false, default);            // plenty on the shelf
        Assert.Equal(0, calm.Sent);
        Assert.Contains("Nothing is about to run out", calm.Note);

        await SetStockAsync(r, 3);                                           // now about a week of cover or less
        r.Sender.Templates.Add(CampaignTypes.AdminRestock);                  // the admin hasn't messaged in 24h: the template carries it
        var run = await r.Runner.RunAsync(camp, false, default);
        Assert.True(run.Sent >= 1);
        var digest = Assert.Single(r.Sender.TemplatesSent);
        Assert.Equal("2349150464707", digest.To);                            // 09150464707 normalised
        Assert.Contains("Adult Dog Food 20kg", digest.Vars["2"]);
        Assert.Contains("Suggested order", digest.Vars["2"]);
        Assert.Contains("Ada", digest.Vars["2"]);                            // personal
        Assert.Contains("biggest buyer", digest.Vars["2"]);
        Assert.Single(r.Email.Sent);
        Assert.Empty(r.Email.Sent[0].Unsub);                                 // an internal digest has no unsubscribe link
    }

    // ------------------------------------------------------------------ wording: dynamic, but only ever from the records

    [Fact]
    public async Task Ai_drafted_wording_is_used_with_the_customers_facts_filled_from_the_database_and_costs_one_call_per_situation()
    {
        var r = await BuildAsync(stock: 8,
            "{\"variants\":[\"Hello {name}, your {product} from {date} must be finished by now. We have it today at {price}.\",\"Good morning {name}! Time to restock {product}?\",\"Hi {name}, {company} here: {product} is ready for you.\"]}");
        await CustomerBoughtAsync(r, "Ngozi Eze", Ngozi, MarketingStatuses.OptedIn, 25, 1, lang: ChatLanguages.English);
        await CustomerBoughtAsync(r, "Ife Ade", "2348055555555", MarketingStatuses.OptedIn, 25, 1, lang: ChatLanguages.English);
        foreach (var p in new[] { Ngozi, "2348055555555" }) await r.Say(p, "hello");      // both have written to us, so the 24-hour window is open and free text is allowed
        r.Sender.Sent.Clear();

        var result = await r.Runner.RunAsync(await r.Campaign(CampaignTypes.Reorder), false, default);
        Assert.Equal(2, result.Sent);
        Assert.Equal(1, r.Ai.Calls);                                         // two customers, same situation and language: one draft, not one per customer
        Assert.All(r.Sender.Sent, s => Assert.Contains("Adult Dog Food 20kg", s.Text));         // {product} was filled from the record, whichever variant was picked
        Assert.Contains(r.Sender.Sent, s => s.Text.Contains("Ngozi"));
        Assert.Contains(r.Sender.Sent, s => s.Text.Contains("Ife"));
    }

    [Fact]
    public void The_writer_rejects_ai_text_that_could_state_a_number_a_link_or_an_unknown_field()
    {
        Assert.True(CampaignWriter.IsSafe("Hello {name}, your {product} must be finished by now. We have it ready for you."));
        Assert.False(CampaignWriter.IsSafe("Hello {name}, enjoy 20% off {product} today only, ready for you now."));          // an invented number
        Assert.False(CampaignWriter.IsSafe("Hello {name}, {product} is now ₦ cheaper, ready for you to collect today."));    // a currency symbol
        Assert.False(CampaignWriter.IsSafe("Hello {name}, order {product} here: https://example.com today, ready for you."));  // a link
        Assert.False(CampaignWriter.IsSafe("Hello {name}, your {product} comes with a {discount} today, ready for you."));    // a field we do not have
        Assert.False(CampaignWriter.IsSafe("Hello there, your {product} must be finished by now, ready for you."));            // not personal
        Assert.False(CampaignWriter.IsSafe("Hello {name},\nyour {product} must be finished by now, ready for you."));           // line break
        Assert.Empty(CampaignWriter.ParseVariants("not json"));
        Assert.Single(CampaignWriter.ParseVariants("{\"variants\":[\"Hello {name}, your {product} must be finished by now.\",\"cheap 50% {name}\"]}"));
    }

    [Fact]
    public void Schedule_runs_a_campaign_once_a_day_inside_its_window_and_honours_start_end_and_every_n_days()
    {
        // 07:30 Lagos = 06:30 UTC
        var c = new Campaign { Enabled = true, RunMinute = 7 * 60 + 30, EveryNDays = 1 };
        DateTime At(int day, int h, int m) => new(2026, 10, day, h, m, 0, DateTimeKind.Utc);
        Assert.False(CampaignSchedule.IsDue(c, At(20, 6, 29)));              // 07:29 Lagos: too early
        Assert.True(CampaignSchedule.IsDue(c, At(20, 6, 30)));
        Assert.True(CampaignSchedule.IsDue(c, At(20, 9, 0)));                // 10:00 Lagos: caught up
        Assert.False(CampaignSchedule.IsDue(c, At(20, 10, 31)));             // 11:31 Lagos: the day is skipped rather than texting at lunch
        c.LastRunOn = new DateOnly(2026, 10, 20);
        Assert.False(CampaignSchedule.IsDue(c, At(20, 6, 45)));              // already ran today
        Assert.True(CampaignSchedule.IsDue(c, At(21, 6, 45)));

        var weekly = new Campaign { Enabled = true, RunMinute = 450, EveryNDays = 7, StartDate = new DateOnly(2026, 10, 19) };
        Assert.False(CampaignSchedule.IsDue(weekly, At(20, 7, 0)));
        Assert.True(CampaignSchedule.IsDue(weekly, At(26, 7, 0)));
        weekly.EndDate = new DateOnly(2026, 10, 25);
        Assert.False(CampaignSchedule.IsDue(weekly, At(26, 7, 0)));          // past the end date
        Assert.False(CampaignSchedule.IsDue(new Campaign { Enabled = false, RunMinute = 450 }, At(20, 7, 0)));
        Assert.True(CampaignSchedule.IsQuietHour(6 * 60 + 59));
        Assert.False(CampaignSchedule.IsQuietHour(9 * 60));
        Assert.True(CampaignSchedule.IsQuietHour(21 * 60));
    }

    [Fact]
    public void A_customers_own_buying_rhythm_beats_the_default_and_unsubscribe_links_cannot_be_forged()
    {
        static DateOnly D(int day) => new(2026, 9, day);
        Assert.Equal(21, CampaignPlanner.RepurchaseInterval([D(1)], 21));                         // one purchase: the default
        Assert.Equal(14, CampaignPlanner.RepurchaseInterval([D(1), D(15), D(29)], 21));           // every two weeks
        Assert.Equal(7, CampaignPlanner.RepurchaseInterval([D(1), D(2)], 21));                    // clamped: never nag within a week

        var token = UnsubscribeLinks.Sign("secret", "chewypets", 42);
        Assert.True(UnsubscribeLinks.Valid("secret", "chewypets", 42, token));
        Assert.False(UnsubscribeLinks.Valid("secret", "chewypets", 43, token));                   // another customer
        Assert.False(UnsubscribeLinks.Valid("secret", "candid", 42, token));                      // another company
        Assert.False(UnsubscribeLinks.Valid("other", "chewypets", 42, token));
        Assert.False(UnsubscribeLinks.Valid("secret", "chewypets", 42, null));
    }
}
