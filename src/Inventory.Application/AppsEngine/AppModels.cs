using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;

namespace Inventory.Application.AppsEngine;

/// <summary>
/// One field of a record. Type: text, textarea, number, money, date, select, bool, email, phone, lookup, lines. json holds structured data a custom screen edits (never shown in the standard form). ReadOnly fields are set by the app's own code (a signature, an invoice number) and survive edits.
/// Lookup: "customer", "employee", "product", or "record:{kind}" (a record of the same app). Lines: product lines (product, quantity, optional price).
/// Fields named title, code, date and amount also fill the record's own columns, so lists can sort, search and total them.
/// </summary>
public sealed record FieldDef(string Key, string Label, string Type = "text", bool Required = false, string[]? Options = null, string? Lookup = null, string? Help = null,
    bool InList = false, string? Default = null, bool ReadOnly = false);

/// <summary>A status a record can be in, how to colour it (ok, warn, bad, info) and which statuses it may move to next. The first status is where new records start.</summary>
public sealed record StatusDef(string Name, string Tone, string[] Next);

/// <summary>A button on a record, run by the app's hook. InStatuses limits where it shows; Prompt asks for a line of text first.</summary>
public sealed record ActionDef(string Key, string Label, string[]? InStatuses = null, string? Confirm = null, bool Primary = false, string? Prompt = null);

/// <param name="Parent">The kind this one belongs to (an event's registrations); null for a top-level kind.</param>
public sealed record KindDef(string Key, string Label, string Plural, string? Parent, FieldDef[] Fields, StatusDef[] Statuses, ActionDef[]? Actions = null, string? Hint = null,
    bool NoCreate = false, bool NoDelete = false);

/// <param name="Permission">The permission code that opens the whole app.</param>
/// <param name="Public">The app publishes something on the public site (a page, a form); the overview shows its address.</param>
public sealed record AppDef(string Key, string Name, string Permission, string Intro, KindDef[] Kinds, string? Public = null);

public sealed record RecordDto(int Id, string Kind, int? ParentId, string Title, string Status, string? Code, DateOnly? Date, decimal? Amount, JsonObject Data,
    IReadOnlyDictionary<string, string> Labels, IReadOnlyList<string> Flags, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record Card(string Label, string Value, string? Sub = null, string Tone = "");
public sealed record AppOverviewDto(AppDef Def, IReadOnlyList<Card> Cards, string? PublicUrl);
public sealed record SaveInput(int? ParentId, JsonObject Fields);
public sealed record ActionInput(string? Text);
/// <param name="Url">Something to open or copy: a public link, a download.</param>
public sealed record ActionResultDto(string? Message, string? Url = null);

/// <summary>What a hook knows about the request it is working in.</summary>
public sealed record HookContext(IBusinessDbContext Db, IClock Clock, CurrentUser User, string CompanyKey, string CompanyName, string BaseUrl, AppDef App)
{
    public DateOnly Today => Clock.BusinessToday;
}

/// <summary>
/// An app's own rules, on top of what every app gets (fields, statuses, lists, forms). Override only what the app needs; throw a
/// <see cref="Inventory.Application.Common.BusinessRuleException"/> to refuse a save or an action with a message the person will read.
/// </summary>
public interface IAppHook
{
    string App { get; }
    /// <summary>After the fields are checked and copied onto the record, before it is saved: compute fields, enforce rules. May change <paramref name="data"/> and the record.</summary>
    Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Before a status change is saved: refuse it if the rules say no (someone approving their own request).</summary>
    Task BeforeStatusAsync(HookContext c, AppRecord r, string to, CancellationToken ct) => Task.CompletedTask;
    /// <summary>After a status change has been saved.</summary>
    Task AfterStatusAsync(HookContext c, AppRecord r, string from, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Before a record is deleted: refuse if it shouldn't be.</summary>
    Task BeforeDeleteAsync(HookContext c, AppRecord r, CancellationToken ct) => Task.CompletedTask;
    Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct) =>
        throw new Inventory.Application.Common.BusinessRuleException("This action isn't available.");
    /// <summary>Extra figures at the top of the app's screen.</summary>
    Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct) => Task.FromResult<IReadOnlyList<Card>>([]);
    /// <summary>Short warnings shown on a row ("Insurance expires in 9 days", "Overdue").</summary>
    IReadOnlyList<string> Flags(HookContext c, AppRecord r, JsonObject data) => [];
}

/// <summary>Reading and writing a record's JSON, with the small conversions every hook needs.</summary>
public static class Dx
{
    public static string? Str(this JsonObject d, string key) => d.TryGetPropertyValue(key, out var n) && n is not null ? (n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToString()) : null;
    public static decimal? Num(this JsonObject d, string key) => d.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<decimal>(out var x) ? x : null;
    public static int? Int(this JsonObject d, string key) => d.Num(key) is decimal x ? (int)x : null;
    public static bool Bool(this JsonObject d, string key) => d.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static DateOnly? Date(this JsonObject d, string key) => DateOnly.TryParse(d.Str(key), out var x) ? x : null;
    public static void Set(this JsonObject d, string key, object? value) => d[key] = value is null ? null : JsonValue.Create(value is DateOnly dt ? dt.ToString("yyyy-MM-dd") : value);
    public static JsonObject Parse(string json) { try { return JsonNode.Parse(json) as JsonObject ?? []; } catch (System.Text.Json.JsonException) { return []; } }
}

