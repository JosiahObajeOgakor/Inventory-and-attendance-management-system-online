using Inventory.Domain;

namespace Inventory.Application.AppsEngine;

/// <summary>Every record-keeping app's definition. The apps are written in the files beside this one, grouped as in the menu.</summary>
public static class AppCatalog
{
    public static readonly IReadOnlyList<AppDef> All =
    [
        .. FinanceApps.All, .. WebsiteApps.All, .. PeopleApps.All, .. MarketingApps.All, .. BroadcastApps.All,
    ];

    public static AppDef? Find(string key) => All.FirstOrDefault(a => a.Key == key);
}

/// <summary>Short builders so a definition reads as a list of fields and statuses.</summary>
internal static class B
{
    public static FieldDef Title(string label = "Title", bool required = true, string? help = null, bool list = true) => new("title", label, "text", required, Help: help, InList: list);
    public static FieldDef Text(string key, string label, bool req = false, bool list = false, string? help = null, string? def = null) => new(key, label, "text", req, Help: help, InList: list, Default: def);
    public static FieldDef Area(string key, string label, bool req = false, string? help = null) => new(key, label, "textarea", req, Help: help);
    public static FieldDef Num(string key, string label, bool req = false, bool list = false, string? def = null, string? help = null) => new(key, label, "number", req, InList: list, Default: def, Help: help);
    public static FieldDef Money(string key, string label, bool req = false, bool list = false, string? help = null) => new(key, label, "money", req, InList: list, Help: help);
    public static FieldDef Date(string key, string label, bool req = false, bool list = false, string? help = null) => new(key, label, "date", req, InList: list, Help: help);
    public static FieldDef Sel(string key, string label, string[] options, bool req = false, bool list = false, string? def = null) => new(key, label, "select", req, options, InList: list, Default: def);
    public static FieldDef Chk(string key, string label, string? help = null, bool list = false) => new(key, label, "bool", false, Help: help, InList: list);
    public static FieldDef Mail(string key, string label, bool req = false, bool list = false) => new(key, label, "email", req, InList: list);
    public static FieldDef Tel(string key, string label, bool req = false, bool list = false) => new(key, label, "phone", req, InList: list);
    public static FieldDef Look(string key, string label, string lookup, bool req = false, bool list = false) => new(key, label, "lookup", req, Lookup: lookup, InList: list);
    public static FieldDef Lines(string key, string label, bool req = false, string? help = null) => new(key, label, "lines", req, Help: help);
    public static FieldDef Ro(string key, string label, string type = "text", bool list = false) => new(key, label, type, InList: list, ReadOnly: true);
    public static FieldDef Json(string key, string label) => new(key, label, "json");
    public static FieldDef Code(string label = "Web address", string? help = "Letters, numbers and dashes. Leave blank to make one from the title.") => new("code", label, "text", false, Help: help);

    public static StatusDef S(string name, string tone, params string[] next) => new(name, tone, next);
    public static ActionDef A(string key, string label, string[]? statuses = null, string? confirm = null, bool primary = false, string? prompt = null) => new(key, label, statuses, confirm, primary, prompt);
    public static string[] In(params string[] s) => s;

    public static KindDef Kind(string key, string label, string plural, FieldDef[] fields, StatusDef[] statuses, ActionDef[]? actions = null, string? parent = null, string? hint = null, bool noDelete = false) =>
        new(key, label, plural, parent, fields, statuses, actions, hint, false, noDelete);
}
