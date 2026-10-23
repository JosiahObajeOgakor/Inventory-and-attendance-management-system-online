using Inventory.Application.Ai;
using Inventory.Application.Company;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Application.SalesAssistant;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class FakeVirtualAccounts : IVirtualAccountGateway
{
    public bool IsConfigured => true;
    public int Created { get; private set; }
    public string? LastOrderId { get; private set; }
    public string? LastTransactionId { get; private set; }
    public Dictionary<string, TransferStatus> Status { get; } = [];
    public Task<VirtualAccount> CreateAsync(string orderId, decimal amount, string description, string firstName, string lastName, string email, string? phone, CancellationToken ct)
    {
        Created++; LastOrderId = orderId; LastTransactionId = "tx-" + Guid.NewGuid().ToString("N")[..12];
        return Task.FromResult(new VirtualAccount("888000" + (7000 + Created), "Wema Bank", LastTransactionId, DateTime.UtcNow.AddHours(24)));
    }
    public Task<TransferStatus?> VerifyAsync(string transactionId, CancellationToken ct) => Task.FromResult(Status.TryGetValue(transactionId, out var s) ? s : null);
    public bool IsValidSignature(string rawBody, string? signature) => signature == "ok";
}

internal sealed class CountingModel(string json = "{\"intent\":\"other\",\"items\":[]}") : IChatModel
{
    public string Json { get; set; } = json;
    public int Calls { get; private set; }
    public bool IsConfigured => true;
    public Task<ChatReply> CompleteAsync(IReadOnlyList<Inventory.Application.Ai.ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken ct)
    { Calls++; return Task.FromResult(new ChatReply(Json, [], 0, 0)); }
}

internal sealed class RecordingAlerts : ISaleAlerts
{
    public List<SaleAlert> Sales { get; } = [];
    public Task SaleCompletedAsync(SaleAlert alert, CancellationToken ct) { Sales.Add(alert); return Task.CompletedTask; }
}

internal sealed class RecordingSender : IChatSender
{
    public List<(string To, string Text)> Sent { get; } = [];
    public bool IsConfigured(string companyKey) => true;
    public Task SendTextAsync(string companyKey, string toDigits, string text, CancellationToken ct) { Sent.Add((toDigits, text)); return Task.CompletedTask; }
    public bool FailNext { get; set; }
    public HashSet<string> Templates { get; } = [];
    public List<(string To, string Key, IReadOnlyDictionary<string, string> Vars)> TemplatesSent { get; } = [];
    public Task<bool> TrySendTextAsync(string companyKey, string toDigits, string text, CancellationToken ct)
    { if (FailNext) { FailNext = false; return Task.FromResult(false); } Sent.Add((toDigits, text)); return Task.FromResult(true); }
    public bool HasTemplate(string companyKey, string templateKey) => Templates.Contains(templateKey);
    public Task<bool> TrySendTemplateAsync(string companyKey, string toDigits, string templateKey, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    { TemplatesSent.Add((toDigits, templateKey, variables)); return Task.FromResult(true); }
}

/// <summary>The whole WhatsApp sale, on a real MySQL: ordering in several languages, stock truth, reservation, the bank account inside the chat, payment confirmation, and the AI-credit ceiling.</summary>
[Collection("mysql")]
public class ChatSalesTests(MySqlFixture mysql)
{
    private const string Ngozi = "2348011111111", Musa = "2348022222222";

    private sealed class Rig
    {
        public required string Cs; public required BusinessDbContext Db; public required ChatFlowService Flow; public required PaymentLinkService Pay; public required ChatPaymentNotifier Notifier;
        public required RecordingAlerts Alerts; public required FakeVirtualAccounts Va; public required CountingModel Ai; public required RecordingSender Sender; public required RecordingNotifier Admin; public required Seed Seed;
        public async Task<string> Say(string phone, string text, string name = "")
        {
            var r = await Flow.HandleAsync(ChatChannels.WhatsApp, phone, name, text, default);
            return string.Join("\n", r.Messages);
        }
    }

    private async Task<Rig> BuildAsync(int stock, string aiJson = "{\"intent\":\"other\",\"items\":[]}", bool withAlerts = false, FakeGateway? gateway = null)
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock);
        var db = NewContext(cs);
        // These tests are about ordering and paying, so the two shoppers have already answered the opt-in question (that step has its own tests in CampaignTests).
        db.Customers.AddRange(new Customer { Name = Ngozi, Phone = Ngozi, CustomerType = "WalkIn", MarketingStatus = MarketingStatuses.OptedIn },
                              new Customer { Name = Musa, Phone = Musa, CustomerType = "WalkIn", MarketingStatus = MarketingStatuses.OptedIn });
        await db.SaveChangesAsync();
        var clock = new SystemClock();
        var co = new CompanyContext(new CompanyInfo("test", "Test Co", "TestCo", "x", "Test Co Ltd", DefaultWarehouseId: seed.WarehouseId));
        var tx = Wire.Tx(db);
        var quotes = new QuotationService(db, tx, clock, co, SalesFor(db), new NoUsers());
        var admin = new RecordingNotifier(); var va = new FakeVirtualAccounts(); var ai = new CountingModel(aiJson); var sender = new RecordingSender();
        var alerts = new RecordingAlerts();
        var pay = new PaymentLinkService(db, tx, clock, co, new PaymentGateways([gateway ?? new FakeGateway(false)]), admin, new CustomerPaymentService(db, tx, clock), quotes, va, withAlerts ? alerts : null);
        var opts = Options.Create(new SalesAssistantOptions());
        var texts = new ChatOrderText(db, co);
        var flow = new ChatFlowService(db, new ChatConversationService(db, clock), new CustomerLookupService(db), quotes, pay, new StockAvailabilityService(db, co, clock, opts),
            new ChatAiInterpreter(ai, db, opts, NullLogger<ChatAiInterpreter>.Instance), texts, new CompanyProfileService(db, co, clock), co, clock, opts, NullLogger<ChatFlowService>.Instance, new Inventory.Application.Campaigns.ConsentService(db, clock));
        return new Rig { Cs = cs, Db = db, Flow = flow, Pay = pay, Alerts = alerts, Va = va, Ai = ai, Sender = sender, Admin = admin, Seed = seed, Notifier = new ChatPaymentNotifier(db, texts, sender, co, clock, NullLogger<ChatPaymentNotifier>.Instance) };
    }

    private static async Task<(PaymentLink Link, Quotation Quote)> OpenOrder(Rig r)
    {
        r.Db.ChangeTracker.Clear();
        var link = await r.Db.PaymentLinks.AsNoTracking().OrderByDescending(l => l.Id).FirstAsync();
        return (link, await r.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == link.DocId));
    }

    private static async Task<int> OnHand(Rig r) { r.Db.ChangeTracker.Clear(); return await r.Db.StockBatches.AsNoTracking().SumAsync(b => b.QuantityOnHand); }

    [Fact]
    public async Task When_the_card_processor_is_set_up_the_customer_also_gets_a_payment_link_and_can_pay_either_way()
    {
        var gateway = new FakeGateway(true);
        var r = await BuildAsync(stock: 10, gateway: gateway);
        await r.Say(Ngozi, "2 adult dog food", "Ngozi");
        var reply = await r.Say(Ngozi, "yes");
        Assert.Contains("8880007001", reply);                          // the bank account, as before
        Assert.Contains("https://paystack.example/", reply);      // and a link to pay by card, USSD or bank on one page
        Assert.Equal(2, await r.Db.PaymentLinks.CountAsync());

        // she pays through the LINK; saying "paid" in the chat finds it (the bank account is checked too)
        var link = await r.Db.PaymentLinks.AsNoTracking().SingleAsync(l => l.Provider == "Paystack");
        gateway.Verification = new GatewayVerification(true, (long)(link.Amount * 100), "NGN", "card");
        var done = await r.Say(Ngozi, "i have paid");
        Assert.Contains("Adult Dog Food 20kg", done);
        Assert.Equal(8, await OnHand(r));
        Assert.Equal(1, await r.Db.Invoices.CountAsync());
    }
    [Fact]
    public async Task A_completed_whatsapp_sale_alerts_the_admin_with_who_bought_what_and_where_the_stock_came_from_and_counts_on_the_dashboard()
    {
        var r = await BuildAsync(stock: 10, withAlerts: true);
        await r.Say(Ngozi, "abeg i wan buy 2 adult dog food", "Ngozi");
        await r.Say(Ngozi, "yes");
        var (link, _) = await OpenOrder(r);
        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, link.Amount, "completed");
        var settled = await r.Pay.SettleAsync(link.Reference, default);
        Assert.True(settled.Applied);

        var alert = Assert.Single(r.Alerts.Sales);
        Assert.Equal("WhatsApp", alert.Channel);
        Assert.Equal("Ngozi", alert.CustomerName);
        Assert.Equal(Ngozi, alert.CustomerPhone);
        Assert.Equal(settled.InvoiceNumber, alert.InvoiceNumber);
        var line = Assert.Single(alert.Lines);
        Assert.Equal((2, "Adult Dog Food 20kg"), (line.Quantity, line.Product));
        Assert.Equal(link.Amount, alert.Paid);
        Assert.Contains("(2)", Assert.Single(alert.StockFrom));                      // the warehouse the two bags really left, with the quantity
        Assert.Empty(r.Admin.Sent);                                                   // the old plain notifier is replaced by the richer alert
        var text = SaleAlertText.Admin(alert);
        Assert.Contains("SALE COMPLETED", text);
        Assert.Contains("Ngozi", text);
        Assert.Contains("2 × Adult Dog Food 20kg", text);
        Assert.Contains("WhatsApp", text);

        r.Db.ChangeTracker.Clear();
        Assert.True(await r.Db.ChatEvents.AnyAsync(e => e.Kind == ChatEventKinds.Sale && e.Reference == settled.InvoiceNumber));
        var summary = await new Inventory.Application.Queries.ChatInsightsQueries(r.Db, new SystemClock()).GetAsync(30, default);
        Assert.Equal((1, 1, 1), (summary.Total.Started, summary.Total.Orders, summary.Total.Sales));
        Assert.Equal(100m, summary.ConversionPct);
    }
    [Fact]
    public async Task A_yoruba_customer_is_answered_in_yoruba_from_the_first_message()
    {
        var r = await BuildAsync(stock: 10);
        var summary = await r.Say(Ngozi, "jowo mo fe ra 2 adult dog food", "Ngozi");
        Assert.Contains("Àṣẹ rẹ", summary);                 // Yoruba summary, not English
        Assert.Contains("2 × Adult Dog Food 20kg", summary);
        var account = await r.Say(Ngozi, "beeni");           // "yes" in Yoruba
        Assert.Contains("8880007001", account);
        Assert.Contains("Jọ̀wọ́ fi", account);
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task A_registered_wholesaler_is_quoted_and_charged_their_own_price_and_a_stranger_the_retail_price()
    {
        var r = await BuildAsync(stock: 40);
        var musa = await r.Db.Customers.SingleAsync(c => c.Phone == Musa);
        musa.CustomerType = "Wholesaler"; await r.Db.SaveChangesAsync();
        var retail = await r.Db.Products.AsNoTracking().Where(p => p.Id == r.Seed.ProductId).Select(p => new { p.PriceRetail, p.PriceWholesaler }).SingleAsync();
        Assert.True(retail.PriceWholesaler < retail.PriceRetail);

        Assert.Contains(ChatOrderText.Naira(retail.PriceWholesaler * 3), await r.Say(Musa, "3 adult dog food", "Musa"));
        Assert.Contains(ChatOrderText.Naira(retail.PriceRetail * 3), await r.Say(Ngozi, "3 adult dog food", "Ngozi"));
    }

    // ------------------------------------------------------------------ the enquiry desk: questions answered in any state, then back to the sale

    private static async Task AddZoneAsync(Rig r, string name, decimal fee, string? eta)
    {
        await using var db = NewContext(r.Cs);
        db.DeliveryZones.Add(new DeliveryZone { Name = name, Fee = fee, Eta = eta, IsActive = true });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_delivery_question_while_an_account_is_open_is_answered_and_delivery_can_be_added_to_the_order()
    {
        var r = await BuildAsync(stock: 10);
        await AddZoneAsync(r, "Agege", 3000, "same day");
        await r.Say(Ngozi, "2 adult dog food", "Ngozi");
        Assert.Contains("8880007001", await r.Say(Ngozi, "yes"));                         // the account for the order without delivery

        var ask = await r.Say(Ngozi, "Hi! Do you deliver to my area, and how much is delivery?");
        Assert.Contains("Agege", ask);                                                     // the question is answered...
        Assert.Contains("Which area", ask);
        Assert.DoesNotContain("haven't seen your payment", ask);                           // ...not met with "pay first"

        var fee = await r.Say(Ngozi, "Agege");
        Assert.Contains("₦3,000", fee);
        Assert.Contains("ADD DELIVERY", fee);
        Assert.Contains("address", await r.Say(Ngozi, "add delivery"));
        var account = await r.Say(Ngozi, "12 Pen Cinema Road, Agege");
        Assert.Contains("12 Pen Cinema Road", account);

        r.Db.ChangeTracker.Clear();
        var (link, quote) = await OpenOrder(r);
        Assert.Equal(3000m, quote.DeliveryFee);
        Assert.Equal("Agege", quote.DeliveryZone);
        Assert.Equal(quote.TotalAmount, link.Amount);                                       // the new account is for the new total, delivery included
        Assert.Equal(1, await r.Db.PaymentLinks.CountAsync(l => l.Status == PaymentLinkStatuses.Pending));   // the old account is closed
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task Delivery_named_before_confirming_goes_on_the_summary_with_its_fee()
    {
        var r = await BuildAsync(stock: 10);
        await AddZoneAsync(r, "Ikeja", 2500, "1-2 days");
        await r.Say(Ngozi, "2 adult dog food", "Ngozi");                                    // summary shown, awaiting yes
        Assert.Contains("address", await r.Say(Ngozi, "do you deliver to ikeja?"));
        var summary = await r.Say(Ngozi, "5 Allen Avenue, Ikeja");
        Assert.Contains("Delivery to Ikeja", summary);
        Assert.Contains("₦2,500", summary);
        Assert.Contains("8880007001", await r.Say(Ngozi, "yes"));
        r.Db.ChangeTracker.Clear();
        var (_, quote) = await OpenOrder(r);
        Assert.Equal("5 Allen Avenue, Ikeja", quote.DeliveryAddress);
    }

    [Fact]
    public async Task Questions_get_short_answers_that_lead_back_to_the_sale()
    {
        var r = await BuildAsync(stock: 10);
        var price = await r.Say(Musa, "how much is adult dog food", "Musa");
        Assert.Contains("How many", price);                                                // price, then straight to "how many?"
        var stock = await r.Say(Musa, "is it available?");                                   // a question in the middle of "how many?"
        Assert.Contains("in stock", stock);
        Assert.Contains("How many", stock);
        Assert.Contains("2 × Adult Dog Food 20kg", await r.Say(Musa, "2"));                 // the sale picks up where it was
        await r.Say(Musa, "cancel");

        Assert.Contains("How many", await r.Say(Musa, "how much is adult dog food"));
        await r.Say(Musa, "do you deliver?");                                               // a detour...
        Assert.Contains("How many", await r.Say(Musa, "where is your shop"));              // ...and back to the product being weighed up

        var hours = await r.Say(Ngozi, "what time do you open", "Ngozi");
        Assert.Contains("e.g.", hours);                                                     // the idle nudge offers to take an order
        var pay = await r.Say(Ngozi, "how do i pay? is it safe");
        Assert.Contains("AlatPay", pay);
        var human = await r.Say(Ngozi, "i want to speak to someone");
        Assert.Contains("team", human);
        Assert.True(await r.Db.ChatEvents.AnyAsync(e => e.Kind == ChatEventKinds.Handoff));
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task A_recommendation_for_a_puppy_names_the_puppy_food_and_asks_how_many()
    {
        var r = await BuildAsync(stock: 10);
        await using (var db = NewContext(r.Cs))
        {
            db.Products.Add(new Product { Sku = "PUP-1", Name = "Puppy Starter 10kg", CategoryId = (await db.Categories.FirstAsync()).Id, PriceRetail = 25000, SellingPrice = 25000, CostPrice = 20000, Species = "Dog", LifeStage = "Puppy" });
            await db.SaveChangesAsync();
            db.StockBatches.Add(new StockBatch { ProductId = (await db.Products.SingleAsync(p => p.Sku == "PUP-1")).Id, WarehouseId = r.Seed.WarehouseId, BatchNumber = "P1", QuantityOnHand = 8 });
            await db.SaveChangesAsync();
        }
        var rec = await r.Say(Ngozi, "which food is best for my puppy?", "Ngozi");
        Assert.Contains("Puppy Starter 10kg", rec);
        Assert.Contains("How many", rec);
        Assert.Contains("3 × Puppy Starter 10kg", await r.Say(Ngozi, "3"));
    }
    [Fact]
    public async Task One_new_quotation_per_number_a_day_and_a_second_request_points_back_to_it()
    {
        var r = await BuildAsync(stock: 20);
        await r.Say(Ngozi, "quote 3 adult dog food", "Ngozi");
        Assert.Contains("Quotation ", await r.Say(Ngozi, "send"));
        await r.Say(Ngozi, "no");
        await r.Say(Ngozi, "quote 5 adult dog food");
        var again = await r.Say(Ngozi, "send");
        Assert.Contains("check back in about an hour", again);
        Assert.Equal(1, await r.Db.Quotations.CountAsync());                     // no second document
        Assert.True(await r.Db.ChatEvents.AnyAsync(e => e.Kind == ChatEventKinds.QuoteLimited));
        Assert.Contains("8880007001", await r.Say(Ngozi, "yes"));                // they can still buy straight away
    }

    [Fact]
    public async Task The_chat_records_each_step_and_where_the_number_is_from()
    {
        var r = await BuildAsync(stock: 20);
        await r.Say(Ngozi, "products", "Ngozi");
        await r.Say(Ngozi, "2 adult dog food");
        r.Db.ChangeTracker.Clear();
        var kinds = await r.Db.ChatEvents.AsNoTracking().Select(e => e.Kind).ToListAsync();
        Assert.Contains(ChatEventKinds.Browsed, kinds);
        Assert.Contains(ChatEventKinds.Selected, kinds);
        Assert.Contains(ChatEventKinds.Summary, kinds);
        var conv = await r.Db.ChatConversations.AsNoTracking().SingleAsync();
        Assert.Equal(2, conv.MessageCount);
        Assert.Equal("Nigeria", conv.Country);
    }

    [Fact]
    public async Task Anyone_can_ask_for_a_quotation_on_whatsapp_and_then_buy_the_same_items()
    {
        var r = await BuildAsync(stock: 20);
        var ask = await r.Say(Ngozi, "quote 3 adult dog food", "Ngozi");
        Assert.Contains("Your quotation", ask);
        Assert.Contains("3 × Adult Dog Food 20kg", ask);
        var ready = await r.Say(Ngozi, "send");
        Assert.Contains("Quotation ", ready);
        Assert.Contains("reply \"yes\"", ready);
        Assert.Equal(1, await r.Db.Quotations.CountAsync());
        Assert.Equal(0, await r.Db.PaymentLinks.CountAsync());                   // a quotation asks nothing of the customer yet
        Assert.Equal(20, await OnHand(r));
        Assert.True(await r.Db.ChatEvents.AnyAsync(e => e.Kind == ChatEventKinds.Quote));

        var account = await r.Say(Ngozi, "yes");                                  // now buy those same items
        Assert.Contains("8880007001", account);
        Assert.True(await r.Db.ChatEvents.AnyAsync(e => e.Kind == ChatEventKinds.Order));
    }

    [Fact]
    public async Task Asking_where_an_order_is_gives_the_waybill_of_that_customers_own_recent_sales_only()
    {
        var r = await BuildAsync(stock: 10);
        var ngozi = await r.Db.Customers.SingleAsync(c => c.Phone == Ngozi);
        var inv = new Invoice { InvoiceNumber = "TestCo-0001", CustomerId = ngozi.Id, InvoiceDate = DateOnly.FromDateTime(DateTime.UtcNow), TotalAmount = 23000, WarehouseId = r.Seed.WarehouseId, CreatedAt = DateTime.UtcNow };
        r.Db.Invoices.Add(inv); await r.Db.SaveChangesAsync();
        r.Db.Waybills.Add(new Waybill { WaybillNumber = "WB-777", InvoiceId = inv.Id, IssueDate = inv.InvoiceDate, DriverName = "Sule", DriverPhone = "08030000000" });
        await r.Db.SaveChangesAsync();

        var mine = await r.Say(Ngozi, "where is my waybill");
        Assert.Contains("WB-777", mine);
        Assert.Contains("Sule", mine);
        var stranger = await r.Say(Musa, "waybill please");
        Assert.DoesNotContain("WB-777", stranger);
        Assert.Equal(2, await r.Db.ChatEvents.CountAsync(e => e.Kind == ChatEventKinds.Waybill));
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task A_pidgin_customer_buys_pays_and_is_confirmed_in_the_chat_with_zero_ai_calls()
    {
        var r = await BuildAsync(stock: 10);

        var summary = await r.Say(Ngozi, "abeg i wan buy 2 adult dog food", "Ngozi");
        Assert.Contains("2 × Adult Dog Food 20kg", summary);
        Assert.Contains("₦23,000", summary);                  // the line: 2 × the product's own retail price
        Assert.Contains("yes / no", summary);

        var account = await r.Say(Ngozi, "yes");
        Assert.Contains("8880007001", account);          // the bank account is shown inside the chat
        Assert.Contains("Wema Bank", account);
        Assert.Equal(1, r.Va.Created);
        Assert.Equal(10, await OnHand(r));               // nothing is deducted before money arrives

        var (link, quote) = await OpenOrder(r);
        Assert.Equal(PaymentLinkStatuses.Pending, link.Status);
        Assert.Equal(quote.TotalAmount, link.Amount);
        Assert.Contains(ChatOrderText.Naira(quote.TotalAmount), summary);   // the total the customer was shown is exactly what the bank account was generated for

        // the bank says the money landed; AlatPay's webhook triggers settlement
        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, link.Amount, "completed");
        var settled = await r.Pay.SettleAsync(link.Reference, default);
        Assert.True(settled.Applied);
        await r.Notifier.NotifyAsync(settled, default);

        var pushed = Assert.Single(r.Sender.Sent);
        Assert.Equal(Ngozi, pushed.To);
        Assert.Contains("don confirm", pushed.Text);     // in Pidgin, because the customer wrote Pidgin
        Assert.Contains("2 × Adult Dog Food 20kg", pushed.Text);
        Assert.Equal(8, await OnHand(r));                // stock now deducted, exactly once
        Assert.Equal(1, await r.Db.Invoices.CountAsync());
        Assert.Equal(QuotationStatuses.Converted, (await r.Db.Quotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, r.Ai.Calls);
        Assert.Single(r.Admin.Sent);

        // a webhook retry changes nothing and sends nothing more
        var again = await r.Pay.SettleAsync(link.Reference, default);
        await r.Notifier.NotifyAsync(again, default);
        Assert.False(again.Applied);
        Assert.Single(r.Sender.Sent);
        Assert.Equal(8, await OnHand(r));
        Assert.Equal(1, await r.Db.Invoices.CountAsync());
    }

    [Fact]
    public async Task The_website_chat_sells_the_same_way_asks_for_a_whatsapp_number_once_and_shows_the_confirmation_on_the_page()
    {
        var r = await BuildAsync(stock: 10);
        async Task<string> Web(string text) => string.Join("\n", (await r.Flow.HandleAsync(ChatChannels.WebChat, "browser-session-1", "", text, default)).Messages);

        Assert.Contains("WhatsApp number", await Web("abeg i wan buy 2 adult dog food"));   // the web has no phone number, so it asks (in Pidgin)
        Assert.Contains("08031234567", await Web("my number na 123"));                        // not a number: a polite example, no guessing
        Assert.Contains("name", await Web("0803 123 4567"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(yes / no)", await Web("Chika Obi"));                                  // first-time customer: the permission question
        Assert.Contains("2 × Adult Dog Food 20kg", await Web("yes"));
        var account = await Web("yes");
        Assert.Contains("8880007001", account);                                                // the bank account, right in the web chat

        r.Db.ChangeTracker.Clear();
        var customer = await r.Db.Customers.AsNoTracking().SingleAsync(c => c.Phone == "2348031234567");
        Assert.Equal("Chika Obi", customer.Name);                                              // stored with the number she typed, normalised
        var (link, _) = await OpenOrder(r);
        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, link.Amount, "completed");
        var settled = await r.Pay.SettleAsync(link.Reference, default);
        await r.Notifier.NotifyAsync(settled, default);

        Assert.Empty(r.Sender.Sent);                                                           // no WhatsApp push to someone who bought on the website…
        r.Db.ChangeTracker.Clear();
        var conv = await r.Db.ChatConversations.AsNoTracking().SingleAsync(c => c.Channel == ChatChannels.WebChat);
        var last = await r.Db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conv.Id).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Contains("don confirm", last.Text);                                             // …the confirmation is in the web conversation, for the page to show
        Assert.Equal(ChatStates.Idle, conv.State);
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task Saying_paid_in_the_chat_checks_with_the_bank_and_confirms_without_waiting_for_the_webhook()
    {
        var r = await BuildAsync(stock: 10);
        await r.Say(Ngozi, "abeg i wan buy 1 adult dog food", "Ngozi");
        await r.Say(Ngozi, "yes");
        var (link, _) = await OpenOrder(r);

        Assert.Contains("never see", await r.Say(Ngozi, "i don pay"));       // bank has nothing yet: honest answer, account repeated
        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, link.Amount, "completed");
        var reply = await r.Say(Ngozi, "i don pay");
        Assert.Contains("don confirm", reply);
        Assert.Equal(9, await OnHand(r));
        Assert.Equal(0, r.Ai.Calls);
    }

    [Theory]
    [InlineData("Ndewo, achọrọ m 1 adult dog food", "Igwe niile")]       // Igbo
    [InlineData("Sannu, ina son 1 adult dog food", "Jimilla")]            // Hausa
    [InlineData("hello I need 1 adult dog food", "Total:")]               // English
    public async Task The_order_is_summarised_in_the_language_the_customer_wrote(string message, string expectedWord)
    {
        var r = await BuildAsync(stock: 5);
        var reply = await r.Say(Ngozi, message, "Ngozi");
        Assert.Contains(expectedWord, reply);
        Assert.Contains("Adult Dog Food 20kg", reply);
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task The_bot_never_sells_more_than_the_database_has_and_offers_what_is_left()
    {
        var r = await BuildAsync(stock: 3);
        var reply = await r.Say(Ngozi, "i need 5 adult dog food", "Ngozi");
        Assert.Contains("only have 3", reply);

        Assert.Contains("3 × Adult Dog Food 20kg", await r.Say(Ngozi, "yes"));   // accepted the 3 that exist
        Assert.Contains("only have 3", await r.Say(Musa, "i need 4 adult dog food", "Musa"));
    }

    [Fact]
    public async Task Stock_promised_to_a_customer_who_has_an_account_is_held_so_the_last_bags_are_not_sold_twice()
    {
        var r = await BuildAsync(stock: 3);
        await r.Say(Ngozi, "i need 2 adult dog food", "Ngozi");
        await r.Say(Ngozi, "yes");                                            // 2 of the 3 are now reserved for Ngozi

        var reply = await r.Say(Musa, "i need 2 adult dog food", "Musa");
        Assert.Contains("only have 1", reply);                                     // Musa is told the truth, not "yes, 2"
        Assert.Contains("1 × Adult Dog Food 20kg", await r.Say(Musa, "yes"));
    }

    [Fact]
    public async Task Stock_that_vanished_between_the_summary_and_yes_is_caught_before_any_account_is_issued()
    {
        var r = await BuildAsync(stock: 5);
        await r.Say(Ngozi, "i need 4 adult dog food", "Ngozi");
        await r.Db.StockBatches.ExecuteUpdateAsync(u => u.SetProperty(b => b.QuantityOnHand, 1));   // someone sold it at the counter

        var reply = await r.Say(Ngozi, "yes");
        Assert.Contains("only have 1", reply);
        Assert.Equal(0, r.Va.Created);
        Assert.Empty(await r.Db.Quotations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Paid_but_stock_gone_is_parked_for_a_person_and_the_customer_is_told()
    {
        var r = await BuildAsync(stock: 5);
        await r.Say(Ngozi, "i need 2 adult dog food", "Ngozi");
        await r.Say(Ngozi, "yes");
        var (link, _) = await OpenOrder(r);
        await r.Db.StockBatches.ExecuteUpdateAsync(u => u.SetProperty(b => b.QuantityOnHand, 0));   // the reservation lapsed and the shop sold out

        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, link.Amount, "completed");
        var settled = await r.Pay.SettleAsync(link.Reference, default);
        Assert.False(settled.Applied);
        Assert.True(settled.Unfulfilled);
        await r.Notifier.NotifyAsync(settled, default);

        r.Db.ChangeTracker.Clear();
        Assert.Equal(PaymentLinkStatuses.PaidUnfulfilled, (await r.Db.PaymentLinks.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await r.Db.Invoices.AsNoTracking().ToListAsync());
        Assert.Contains("PAID BUT NOT FULFILLED", r.Admin.Sent.Single().Subject);
        Assert.Contains("refund", Assert.Single(r.Sender.Sent).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_underpayment_is_not_recorded_and_the_customer_is_told_how_much_is_missing()
    {
        var r = await BuildAsync(stock: 5);
        await r.Say(Ngozi, "i need 2 adult dog food", "Ngozi");
        await r.Say(Ngozi, "yes");
        var (link, _) = await OpenOrder(r);

        r.Va.Status[link.ExternalId!] = new TransferStatus(true, link.Amount, 10000m, "completed");
        var settled = await r.Pay.SettleAsync(link.Reference, default);
        Assert.False(settled.Applied);
        Assert.Equal(link.Amount - 10000m, settled.Shortfall);
        await r.Notifier.NotifyAsync(settled, default);
        Assert.Contains(ChatOrderText.Naira(link.Amount - 10000m), Assert.Single(r.Sender.Sent).Text);   // the shortfall, VAT included
        Assert.Equal(5, await OnHand(r));
        Assert.Empty(await r.Db.Invoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_new_customer_is_asked_for_a_name_once_and_it_is_stored()
    {
        var r = await BuildAsync(stock: 5);
        Assert.Contains("name", await r.Say(Ngozi, "2 adult dog food"), StringComparison.OrdinalIgnoreCase);
        var summary = await r.Say(Ngozi, "my name is chidinma okafor");
        Assert.Contains("Total:", summary);
        await r.Say(Ngozi, "yes");
        r.Db.ChangeTracker.Clear();
        Assert.Equal("Chidinma Okafor", (await r.Db.Customers.AsNoTracking().SingleAsync(c => c.Phone == Ngozi)).Name);
    }

    [Fact]
    public async Task The_basket_sent_from_the_website_shop_becomes_the_order_in_one_message()
    {
        var r = await BuildAsync(stock: 10);
        await using (var db = NewContext(r.Cs))
        {
            db.Products.Add(new Product { Sku = "SKU-2", Name = "Adult Dog Food 5kg", CategoryId = (await db.Categories.FirstAsync()).Id, PriceRetail = 3500, SellingPrice = 3500, CostPrice = 2500 });
            await db.SaveChangesAsync();
            db.StockBatches.Add(new StockBatch { ProductId = (await db.Products.SingleAsync(p => p.Sku == "SKU-2")).Id, WarehouseId = r.Seed.WarehouseId, BatchNumber = "B2", QuantityOnHand = 9 });
            await db.SaveChangesAsync();
        }
        // exactly what the landing page's "Order on WhatsApp" button types
        var reply = await r.Say(Ngozi, "Hi! I'd like to order 2 Adult Dog Food 20kg and 3 Adult Dog Food 5kg", "Ngozi");
        Assert.Contains("2 × Adult Dog Food 20kg", reply);
        Assert.Contains("3 × Adult Dog Food 5kg", reply);
        Assert.Equal(0, r.Ai.Calls);
    }

    [Fact]
    public async Task Ambiguous_and_unknown_products_are_asked_about_by_name_never_by_a_numbered_menu()
    {
        var r = await BuildAsync(stock: 5);
        await using (var db = NewContext(r.Cs))
        {
            db.Products.Add(new Product { Sku = "SKU-2", Name = "Adult Dog Food 5kg", CategoryId = (await db.Categories.FirstAsync()).Id, PriceRetail = 3500, SellingPrice = 3500, CostPrice = 2500 });
            await db.SaveChangesAsync();
            db.StockBatches.Add(new StockBatch { ProductId = (await db.Products.SingleAsync(p => p.Sku == "SKU-2")).Id, WarehouseId = r.Seed.WarehouseId, BatchNumber = "B2", QuantityOnHand = 9 });
            await db.SaveChangesAsync();
        }
        var ask = await r.Say(Ngozi, "2 adult dog food", "Ngozi");
        Assert.Contains("Adult Dog Food 20kg / Adult Dog Food 5kg", ask);
        Assert.DoesNotMatch(@"(?i)reply\s+\d|press\s+\d|^\s*[12]\.", ask);
        Assert.Contains("2 × Adult Dog Food 5kg", await r.Say(Ngozi, "the 5kg one", "Ngozi"));
        Assert.Contains("pizza", await r.Say(Musa, "pizza", "Musa"));
        Assert.Equal(1, r.Ai.Calls);                                         // "pizza" was the one thing words could not settle
    }

    [Fact]
    public async Task The_ai_can_never_be_called_more_than_ten_times_for_one_sale()
    {
        var r = await BuildAsync(stock: 5);
        string last = "";
        for (var i = 0; i < 15; i++) last = await r.Say(Ngozi, $"zzqx kwtp {i}", "Ngozi");
        Assert.Equal(10, r.Ai.Calls);
        Assert.Contains("team will reply", last);                            // after the ceiling the customer is handed to a person, no more spend
    }

    [Fact]
    public async Task When_words_fail_the_ai_picks_a_product_id_but_price_and_stock_still_come_from_the_database()
    {
        var r = await BuildAsync(stock: 5);
        r.Ai.Json = $"{{\"intent\":\"order\",\"language\":\"igl\",\"items\":[{{\"id\":{r.Seed.ProductId},\"qty\":2}},{{\"id\":99999,\"qty\":1}}]}}";
        var reply = await r.Say(Ngozi, "kpom okpa gba eji le", "Ngozi");      // not words we know
        Assert.Contains("2 × Adult Dog Food 20kg", reply);
        Assert.Contains("₦23,000", reply);                                    // the price is the product record's, the invented id 99999 is ignored
        Assert.Equal(1, r.Ai.Calls);
    }

    [Fact]
    public async Task Cancel_clears_the_draft_and_the_ai_budget()
    {
        var r = await BuildAsync(stock: 5);
        await r.Say(Ngozi, "zzqx kwtp", "Ngozi");
        await r.Say(Ngozi, "2 adult dog food");
        Assert.Contains("cancel", await r.Say(Ngozi, "cancel"), StringComparison.OrdinalIgnoreCase);
        r.Db.ChangeTracker.Clear();
        var c = await r.Db.ChatConversations.AsNoTracking().SingleAsync();
        Assert.Equal(ChatStates.Idle, c.State);
        Assert.Equal(0, c.AiCalls);
    }
}
