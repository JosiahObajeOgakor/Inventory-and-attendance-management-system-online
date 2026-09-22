using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Application.Common;
using Inventory.Application.Dashboard;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

internal sealed class MemoryUsage : IAiUsageStore
{
    private readonly List<(long Id, int User, DateTime At)> _rows = [];
    private long _n;
    public Task<IReadOnlyList<DateTime>> RecentAsync(int userId, TimeSpan window, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateTime>>(_rows.Where(r => r.User == userId && r.At > DateTime.UtcNow - window).OrderBy(r => r.At).Select(r => r.At).ToList());
    public Task<long> ReserveAsync(int userId, CancellationToken ct) { var id = ++_n; _rows.Add((id, userId, DateTime.UtcNow)); return Task.FromResult(id); }
    public Task ReleaseAsync(long id, CancellationToken ct) { _rows.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
    public int Count => _rows.Count;
}

/// <summary>A scripted model: first asks for a tool, then answers using whatever the tool returned.</summary>
internal sealed class ScriptedModel(string tool = "business_overview", bool fail = false) : IChatModel
{
    public bool IsConfigured => true;
    public int Calls { get; private set; }
    public string? LastToolResult { get; private set; }
    public string? SystemPrompt { get; private set; }
    public Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken ct)
    {
        Calls++;
        if (fail) throw new BusinessRuleException("openai down");
        SystemPrompt = messages[0].Content;
        var toolMsg = messages.LastOrDefault(m => m.Role == "tool");
        if (toolMsg is null && tools.Count > 0) return Task.FromResult(new ChatReply(null, [new ToolCall("c1", tool, "{}")], 0, 0));
        LastToolResult = toolMsg?.Content;
        return Task.FromResult(new ChatReply("Here is your answer.", [], 0, 0));
    }
}

[Collection("mysql")]
public class AssistantTests(MySqlFixture mysql)
{
    private static readonly CurrentUser Boss = new(7, "Ada Admin", "ADMIN");

    private static AssistantService Build(BusinessDbContext db, IChatModel model, IAiUsageStore usage, int limit = 10)
    {
        var clock = new SystemClock();
        var co = Wire.Co();
        var tools = new AiToolbox(db, clock, new OverviewQueries(db, clock));
        return new AssistantService(model, usage, tools, clock, co, Options.Create(new AiOptions { ApiKey = "test", DailyMessageLimit = limit }));
    }

    [Fact]
    public async Task The_model_reads_this_companys_records_through_a_tool_and_the_answer_comes_back()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 10);
        await using (var s = NewContext(cs)) await SalesFor(s).SaveAsync(Sale(seed, 2, paid: 5000), Clerk, null);

        await using var db = NewContext(cs);
        var model = new ScriptedModel();
        var a = await Build(db, model, new MemoryUsage()).AskAsync(Boss, "How are sales?", null, default);

        Assert.Equal("Here is your answer.", a.Answer);
        Assert.Equal(9, a.Remaining);
        Assert.Contains("inventoryValue", model.LastToolResult);
        Assert.Contains("only from this business", model.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SpringuptechAfrica Limited", model.SystemPrompt);
    }

    [Fact]
    public async Task The_eleventh_question_in_a_day_is_refused_without_calling_the_model_and_says_when_to_come_back()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var usage = new MemoryUsage(); var model = new ScriptedModel();
        var svc = Build(db, model, usage);
        for (var i = 0; i < 10; i++) Assert.False((await svc.AskAsync(Boss, $"q{i}", null, default)).Limited);
        var calls = model.Calls;

        var eleventh = await svc.AskAsync(Boss, "one more", null, default);
        Assert.True(eleventh.Limited);
        Assert.Equal(0, eleventh.Remaining);
        Assert.Contains("Come back", eleventh.Answer);
        Assert.NotNull(eleventh.NextAvailableUtc);
        Assert.Equal(calls, model.Calls);   // no OpenAI spend once the allowance is used up
        Assert.Equal(10, usage.Count);
    }

    [Fact]
    public async Task Each_person_has_their_own_allowance()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var usage = new MemoryUsage(); var svc = Build(db, new ScriptedModel(), usage, limit: 1);
        Assert.False((await svc.AskAsync(Boss, "a", null, default)).Limited);
        Assert.True((await svc.AskAsync(Boss, "b", null, default)).Limited);
        Assert.False((await svc.AskAsync(new CurrentUser(8, "Other Admin", "ADMIN"), "c", null, default)).Limited);
    }

    [Fact]
    public async Task A_failed_question_does_not_use_up_the_allowance()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var usage = new MemoryUsage();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Build(db, new ScriptedModel(fail: true), usage).AskAsync(Boss, "hello", null, default));
        Assert.Equal(0, usage.Count);
    }

    [Fact]
    public async Task Blank_and_oversized_questions_are_rejected_before_any_allowance_is_taken()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var usage = new MemoryUsage(); var svc = Build(db, new ScriptedModel(), usage);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.AskAsync(Boss, "   ", null, default));
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.AskAsync(Boss, new string('x', 501), null, default));
        Assert.Equal(0, usage.Count);
    }

    [Fact]
    public async Task Only_the_declared_read_only_tools_exist_and_unknown_ones_return_an_error()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var tools = new AiToolbox(db, new SystemClock(), new OverviewQueries(db, new SystemClock()));
        Assert.All(AiToolbox.Specs, s => { using var _ = JsonDocument.Parse(s.ParametersJsonSchema); });
        Assert.Contains("Unknown tool", await tools.ExecuteAsync("drop_database", "{}", default));
        Assert.Contains("Could not read", await tools.ExecuteAsync("sales_by_day", "{not json", default));
    }

    [Fact]
    public async Task Customer_scores_reward_frequent_on_time_buyers_and_hold_credit_for_late_payers()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 200);
        int lateId;
        await using (var db = NewContext(cs))
        {
            var late = new Customer { Name = "Slow Payer Ltd", CustomerType = "Retailer", RebateRatePct = 1 };
            db.Customers.Add(late); await db.SaveChangesAsync(); lateId = late.Id;
        }
        // The seeded customer buys eight times and pays each time; the late one buys once and pays nothing, overdue.
        for (var i = 0; i < 8; i++) { await using var s = NewContext(cs); await SalesFor(s).SaveAsync(Sale(seed, 5, paid: 100000), Clerk, null); }
        await using (var s = NewContext(cs))
        {
            var req = Sale(seed, 3); req.CustomerId = lateId; req.DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-20);
            await SalesFor(s).SaveAsync(req, Clerk, null);
        }

        await using var q = NewContext(cs);
        var scores = await new AiToolbox(q, new SystemClock(), new OverviewQueries(q, new SystemClock())).CustomerScores(null, default);
        var good = scores.Single(x => x.Name == "PetMart"); var bad = scores.Single(x => x.Name == "Slow Payer Ltd");
        Assert.True(good.Score > bad.Score);
        Assert.Equal(0, good.OverdueInvoices);
        Assert.Equal(1, bad.OverdueInvoices);
        Assert.Contains("Hold new credit", bad.Recommendation);
    }

    [Fact]
    public async Task Overview_reports_inventory_by_warehouse_and_flags_overdue_money_and_calendar_items()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 20);
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);
        await using (var s = NewContext(cs)) { var r = Sale(seed, 2); r.DueDate = due; await SalesFor(s).SaveAsync(r, Clerk, null); }

        await using var db = NewContext(cs);
        var clock = new SystemClock(); var oq = new OverviewQueries(db, clock);
        var o = await oq.GetAsync(30, default);
        Assert.Equal(18, o.Warehouses.Single().Units);
        Assert.Equal(18 * 8500m, o.InventoryValue);
        Assert.True(o.OverdueTotal > 0);
        Assert.Contains(o.Signals, s => s.Kind == "overdue");
        Assert.Equal(30, o.Days.Count); Assert.Equal(12, o.Months.Count);

        var cal = await oq.CalendarAsync(due.Year, due.Month, default);
        Assert.Contains(cal.Overdue, i => i.Customer == "PetMart" && i.DaysOverdue >= 5);
    }
}
