using Inventory.Application.Ai;
using Inventory.Application.Common;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Application.SalesAssistant;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

/// <summary>A model that answers in plain text (no tools), optionally failing its first N calls.</summary>
internal sealed class PlainModel(int failFirst = 0) : IChatModel
{
    public int Calls { get; private set; }
    public bool IsConfigured => true;
    public Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken ct)
    {
        Calls++;
        if (Calls <= failFirst) throw new BusinessRuleException("The assistant can't reach OpenAI right now.");
        return Task.FromResult(new ChatReply("Reply to: " + messages[^1].Content, [], 0, 0));
    }
}

/// <summary>Records what would go out over WhatsApp; can fail its first N text sends.</summary>
internal sealed class FlakyWhatsApp(int failFirstSends = 0) : IWhatsAppSender
{
    private int _attempts;
    public List<(string To, string Text)> Texts { get; } = [];
    public bool IsConfigured(string companyKey) => true;
    public Task SendTextAsync(string companyKey, string toE164, string text, CancellationToken ct)
    {
        if (++_attempts <= failFirstSends) throw new HttpRequestException("Meta unavailable");
        Texts.Add((toE164, text)); return Task.CompletedTask;
    }
    public Task SendDocumentAsync(string companyKey, string toE164, byte[] pdf, string filename, string caption, CancellationToken ct) => Task.CompletedTask;
    public Task SendButtonsAsync(string companyKey, string toE164, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> SendTemplateAsync(string companyKey, string toE164, string templateName, string languageCode, IReadOnlyList<string> bodyParameters, CancellationToken ct) => Task.FromResult(true);
}

[Collection("mysql")]
public class InboxTests(MySqlFixture mysql)
{
    private static readonly SystemClock Clock = new();
    private static readonly IOptions<InboxOptions> Opt = Options.Create(new InboxOptions { MaxAttempts = 3, Concurrency = 4 });
    private const string Wa = ChatChannels.WhatsApp;

    private static WhatsAppInbox Inbox(BusinessDbContext db) => new(db, Wire.Tx(db), Clock, Opt);

    private static WhatsAppInboxProcessor Processor(BusinessDbContext db, IChatModel model, IWhatsAppSender wa, RecordingNotifier admin)
    {
        // The model answers without tools, so the toolbox's own dependencies are never touched.
        var tools = new SalesAssistantToolbox(db, null!, null!, null!, null!, new ChatConversationService(db, Clock), null!, null!, Wire.Co(), null!, null!);
        var assistant = new SalesAssistantService(model, tools, new ChatConversationService(db, Clock), Wire.Co(), Clock, Options.Create(new SalesAssistantOptions()));
        return new WhatsAppInboxProcessor(db, assistant, wa, Wire.Co(), admin, Clock, Opt,
            Options.Create(new FulfilmentOptions { AdminWhatsApp = "09150464707" }), NullLogger<WhatsAppInboxProcessor>.Instance);
    }

    /// <summary>Makes every pending message due now (skips the retry wait).</summary>
    private static Task DueNow(string cs) => NewContext(cs).InboundMessages.ExecuteUpdateAsync(u => u.SetProperty(m => m.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));

    [Fact]
    public async Task A_redelivered_message_is_stored_once()
    {
        var cs = await mysql.NewSchemaAsync();
        await using (var db = NewContext(cs)) Assert.True(await Inbox(db).EnqueueAsync(Wa, "2348030000000", "wamid.1", "hi", null, default));
        await using (var db = NewContext(cs)) Assert.False(await Inbox(db).EnqueueAsync(Wa, "2348030000000", "wamid.1", "hi", null, default));
        Assert.Single(await NewContext(cs).InboundMessages.ToListAsync());
    }

    [Fact]
    public async Task Each_customer_is_answered_one_message_at_a_time_in_order_and_two_workers_never_take_the_same_one()
    {
        var cs = await mysql.NewSchemaAsync();
        await using (var db = NewContext(cs))
        {
            var inbox = Inbox(db);
            await inbox.EnqueueAsync(Wa, "A", "a1", "first from A", null, default);
            await inbox.EnqueueAsync(Wa, "A", "a2", "second from A", null, default);
            await inbox.EnqueueAsync(Wa, "B", "b1", "first from B", null, default);
        }
        List<long> w1, w2;
        await using (var db = NewContext(cs)) w1 = await Inbox(db).ClaimAsync(10, default);
        await using (var db = NewContext(cs)) w2 = await Inbox(db).ClaimAsync(10, default);

        await using var check = NewContext(cs);
        var claimed = await check.InboundMessages.Where(m => w1.Contains(m.Id)).Select(m => m.ExternalId).ToListAsync();
        Assert.Equal(["a1", "b1"], claimed.Order());   // A's second message waits for the first
        Assert.Empty(w2);                               // leased: a second worker gets nothing
    }

    [Fact]
    public async Task A_message_is_answered_and_marked_done()
    {
        var cs = await mysql.NewSchemaAsync();
        await using (var db = NewContext(cs)) await Inbox(db).EnqueueAsync(Wa, "2348030000000", "w1", "Do you have kitten food?", null, default);
        var wa = new FlakyWhatsApp(); var model = new PlainModel();
        await using (var db = NewContext(cs)) { var id = Assert.Single(await Inbox(db).ClaimAsync(4, default)); await Processor(db, model, wa, new()).ProcessAsync(id, default); }

        Assert.Equal(("2348030000000", "Reply to: Do you have kitten food?"), Assert.Single(wa.Texts));
        var m = await NewContext(cs).InboundMessages.SingleAsync();
        Assert.Equal((InboundStatuses.Done, 1), (m.Status, m.Attempts));
        Assert.NotNull(m.ProcessedAt);
    }

    [Fact]
    public async Task A_failed_send_is_retried_with_the_same_reply_and_the_model_is_not_asked_twice()
    {
        var cs = await mysql.NewSchemaAsync();
        await using (var db = NewContext(cs)) await Inbox(db).EnqueueAsync(Wa, "2348030000000", "w1", "Price of senior formula?", null, default);
        var wa = new FlakyWhatsApp(failFirstSends: 1); var model = new PlainModel();

        await using (var db = NewContext(cs)) { var id = Assert.Single(await Inbox(db).ClaimAsync(4, default)); await Processor(db, model, wa, new()).ProcessAsync(id, default); }
        var after1 = await NewContext(cs).InboundMessages.SingleAsync();
        Assert.Equal(InboundStatuses.Pending, after1.Status);
        Assert.True(after1.NextAttemptAt > DateTime.UtcNow);          // backing off
        Assert.Empty(await Inbox(NewContext(cs)).ClaimAsync(4, default));   // not due yet

        await DueNow(cs);
        await using (var db = NewContext(cs)) { var id = Assert.Single(await Inbox(db).ClaimAsync(4, default)); await Processor(db, model, wa, new()).ProcessAsync(id, default); }
        Assert.Single(wa.Texts);
        Assert.Equal(1, model.Calls);                                   // the saved reply was re-sent
        Assert.Equal(InboundStatuses.Done, (await NewContext(cs).InboundMessages.SingleAsync()).Status);
    }

    [Fact]
    public async Task After_the_last_attempt_the_customer_gets_an_apology_and_the_admin_is_alerted()
    {
        var cs = await mysql.NewSchemaAsync();
        await using (var db = NewContext(cs)) await Inbox(db).EnqueueAsync(Wa, "2348030000000", "w1", "I want to order", null, default);
        var wa = new FlakyWhatsApp(); var model = new PlainModel(failFirst: 99); var admin = new RecordingNotifier();

        for (var i = 0; i < 3; i++)
        {
            await DueNow(cs);
            await using var db = NewContext(cs);
            var id = Assert.Single(await Inbox(db).ClaimAsync(4, default));
            await Processor(db, model, wa, admin).ProcessAsync(id, default);
        }

        var m = await NewContext(cs).InboundMessages.SingleAsync();
        Assert.Equal((InboundStatuses.Failed, 3), (m.Status, m.Attempts));
        Assert.Contains("OpenAI", m.LastError);
        Assert.Contains(wa.Texts, t => t.To == "2348030000000" && t.Text.Contains("member of our team"));   // the customer isn't left hanging
        Assert.DoesNotContain(wa.Texts, t => t.To == "2348030000000" && t.Text.Contains("OpenAI"));          // and never sees the raw error
        Assert.Contains("I want to order", Assert.Single(admin.Sent).Message);
        Assert.Contains(wa.Texts, t => t.To == "2349150464707");                                               // WhatsApp alert to the admin too
        await DueNow(cs);
        Assert.Empty(await Inbox(NewContext(cs)).ClaimAsync(4, default));                                      // a failed message is never retried
    }

    [Fact]
    public async Task Old_answered_messages_are_purged_but_pending_ones_are_kept()
    {
        var cs = await mysql.NewSchemaAsync();
        await using (var db = NewContext(cs))
        {
            db.InboundMessages.AddRange(
                new InboundMessage { Sender = "A", ExternalId = "old-done", Status = InboundStatuses.Done, ReceivedAt = DateTime.UtcNow.AddDays(-40), NextAttemptAt = DateTime.UtcNow },
                new InboundMessage { Sender = "A", ExternalId = "old-pending", Status = InboundStatuses.Pending, ReceivedAt = DateTime.UtcNow.AddDays(-40), NextAttemptAt = DateTime.UtcNow },
                new InboundMessage { Sender = "A", ExternalId = "new-done", Status = InboundStatuses.Done, ReceivedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext(cs)) Assert.Equal(1, await Inbox(db).PurgeAsync(default));
        Assert.Equal(["new-done", "old-pending"], (await NewContext(cs).InboundMessages.Select(m => m.ExternalId).ToListAsync()).Order());
    }
}
