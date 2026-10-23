using System.Globalization;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

/// <summary>
/// Stores, checks and lists the records of every record-keeping app, from the app's definition (<see cref="AppCatalog"/>). What a field may hold, which statuses
/// a record can move between and who may see the app are all decided here, once; an app's own rules live in its <see cref="IAppHook"/>.
/// </summary>
public sealed partial class AppRecordService(IBusinessDbContext db, IClock clock, ICompanyContext company, IEnumerable<IAppHook> hooks, Partners.IStaffAlerts? alerts = null)
{
    private const int Max = 1000;

    public static AppDef Def(string app) => AppCatalog.Find(app) ?? throw new NotFoundException("App");
    private static KindDef KindOf(AppDef d, string kind) => d.Kinds.FirstOrDefault(k => k.Key == kind) ?? throw new NotFoundException("Record type");
    private IAppHook? Hook(string app) => hooks.FirstOrDefault(h => h.App == app);
    private HookContext Ctx(AppDef d, CurrentUser user, string baseUrl) => new(db, clock, user, company.Key, company.LegalName, baseUrl, d);

    // ================================================================ reading

    public async Task<AppOverviewDto> OverviewAsync(string app, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app);
        var cards = new List<Card>();
        foreach (var k in d.Kinds.Where(k => k.Parent is null).Take(2))
        {
            var counts = await db.AppRecords.AsNoTracking().Where(r => r.App == app && r.Kind == k.Key).GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            cards.Add(new Card(k.Plural, counts.Sum(c => c.N).ToString(CultureInfo.InvariantCulture)));
            foreach (var s in k.Statuses.Where(s => s.Tone is "warn" or "ok").Take(2))
                cards.Add(new Card($"{k.Plural}: {s.Name.ToLowerInvariant()}", (counts.FirstOrDefault(c => c.Key == s.Name)?.N ?? 0).ToString(CultureInfo.InvariantCulture), null, s.Tone));
        }
        if (Hook(app) is { } h) cards.AddRange(await h.CardsAsync(Ctx(d, user, baseUrl), ct));
        return new AppOverviewDto(d, cards, d.Public is null ? null : baseUrl + d.Public);
    }

    public async Task<List<RecordDto>> ListAsync(string app, string kind, int? parentId, string? status, string? q, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app); var k = KindOf(d, kind);
        var query = db.AppRecords.AsNoTracking().Where(r => r.App == app && r.Kind == kind);
        if (parentId is int p) query = query.Where(r => r.ParentId == p);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(r => r.Title.Contains(s) || (r.Code != null && r.Code.Contains(s)) || r.Data.Contains(s)); }
        var rows = await query.OrderByDescending(r => r.Date ?? DateOnly.MinValue).ThenByDescending(r => r.Id).Take(Max).ToListAsync(ct);
        return await ToDtosAsync(d, k, rows, user, baseUrl, ct);
    }

    public async Task<RecordDto> GetAsync(string app, int id, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app);
        var r = await db.AppRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.App == app, ct) ?? throw new NotFoundException("Record");
        return (await ToDtosAsync(d, KindOf(d, r.Kind), [r], user, baseUrl, ct))[0];
    }

    private async Task<List<RecordDto>> ToDtosAsync(AppDef d, KindDef k, List<AppRecord> rows, CurrentUser user, string baseUrl, CancellationToken ct)
    {
        var datas = rows.ToDictionary(r => r.Id, r => Dx.Parse(r.Data));
        // Lookups store ids; the list shows names. Resolve each kind of lookup in one query.
        var labels = new Dictionary<(string Lookup, int Id), string>();
        foreach (var f in k.Fields.Where(f => f.Type == "lookup" && f.Lookup is not null))
        {
            var ids = datas.Values.Select(v => v.Int(f.Key)).Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
            if (ids.Count == 0) continue;
            var names = f.Lookup switch
            {
                "customer" => await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, Name = c.Name }).ToListAsync(ct),
                "employee" => await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => new { e.Id, Name = e.FullName }).ToListAsync(ct),
                "product" => await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, Name = p.Name }).ToListAsync(ct),
                _ when f.Lookup.StartsWith("record:") => await db.AppRecords.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, Name = x.Title }).ToListAsync(ct),
                _ => [],
            };
            foreach (var n in names) labels[(f.Lookup!, n.Id)] = n.Name;
        }
        var hook = Hook(d.Key); var ctx = Ctx(d, user, baseUrl);
        var parentTitles = k.Parent is null ? [] : await db.AppRecords.AsNoTracking().Where(x => rows.Select(r => r.ParentId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Title, ct);
        return rows.Select(r =>
        {
            var data = datas[r.Id];
            var map = new Dictionary<string, string>();
            foreach (var f in k.Fields.Where(f => f.Type == "lookup" && f.Lookup is not null))
                if (data.Int(f.Key) is int id && labels.TryGetValue((f.Lookup!, id), out var name)) map[f.Key] = name;
            if (r.ParentId is int pid && parentTitles.TryGetValue(pid, out var pt)) map["_parent"] = pt;
            return new RecordDto(r.Id, r.Kind, r.ParentId, r.Title, r.Status, r.Code, r.Date, r.Amount, data, map, hook?.Flags(ctx, r, data) ?? [], r.CreatedByName, r.CreatedAt, r.UpdatedAt);
        }).ToList();
    }

    // ================================================================ writing

    public async Task<int> SaveAsync(string app, string kind, int? id, SaveInput input, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app); var k = KindOf(d, kind);
        AppRecord r;
        var isNew = id is null;
        if (isNew)
        {
            r = new AppRecord { App = app, Kind = kind, Status = k.Statuses[0].Name, CreatedByUserId = user.Id, CreatedByName = user.FullName, CreatedAt = clock.UtcNow };
            if (k.Parent is not null)
            {
                if (input.ParentId is not int pid || !await db.AppRecords.AnyAsync(x => x.Id == pid && x.App == app && x.Kind == k.Parent, ct)) throw new BusinessRuleException($"Choose which {k.Parent} this belongs to.");
                r.ParentId = pid;
            }
            db.AppRecords.Add(r);
        }
        else
        {
            r = await db.AppRecords.SingleOrDefaultAsync(x => x.Id == id && x.App == app && x.Kind == kind, ct) ?? throw new NotFoundException("Record");
            if (r.Status == AwaitingApproval && NeedsApproval(user)) throw new BusinessRuleException("This is waiting for an administrator's approval. Ask them to reject it if you need to change it.");
        }

        var data = new JsonObject();
        var before = isNew ? new JsonObject() : Dx.Parse(r.Data);
        // bookkeeping the engine keeps on a record (approval request, rejection note) isn't a form field: keep it
        foreach (var (key, value) in before.Where(kv => kv.Key.StartsWith('_')).ToList()) data[key] = value?.DeepClone();
        foreach (var f in k.Fields)
        {
            // Fields the app's own code sets (a signature, an invoice number) are never taken from the form: they keep what they had.
            if (f.ReadOnly) { if (before[f.Key] is { } keep) data[f.Key] = keep.DeepClone(); continue; }
            var v = await CleanAsync(app, f, input.Fields[f.Key], ct);
            if (v is not null) data[f.Key] = v;
        }
        r.Title = Text(data, "title") ?? "";   // a hook may fill a blank title (a review is titled by its employee)
        data.Remove("title");
        if (k.Fields.Any(f => f.Key == "code")) { r.Code = Text(data, "code")?.ToLowerInvariant(); data.Remove("code"); }
        if (k.Fields.Any(f => f.Key == "date")) { r.Date = data.Date("date"); data.Remove("date"); }
        if (k.Fields.Any(f => f.Key == "amount")) { r.Amount = data.Num("amount"); data.Remove("amount"); }
        r.UpdatedAt = clock.UtcNow;

        if (Hook(app) is { } h) await h.BeforeSaveAsync(Ctx(d, user, baseUrl), r, data, isNew, ct);
        if (string.IsNullOrWhiteSpace(r.Title)) throw new BusinessRuleException("Enter a title.");
        if (r.Title.Length > 200) r.Title = r.Title[..200];
        if (r.Code is not null && await db.AppRecords.AnyAsync(x => x.App == app && x.Kind == kind && x.Code == r.Code && x.Id != r.Id, ct))
            throw new BusinessRuleException($"The address \"{r.Code}\" is already used. Choose another.");
        r.Data = data.ToJsonString();
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = $"{app.ToUpperInvariant()}_{(isNew ? "CREATED" : "EDITED")}", Entity = "AppRecord", At = clock.UtcNow, Detail = $"{k.Label}: {r.Title}" });
        await db.SaveChangesAsync(ct);
        return r.Id;
    }

    private static string? Text(JsonObject d, string key) => d.Str(key) is { Length: > 0 } s ? s : null;

    /// <summary>Checks one field against its definition and returns the value to store (null = leave it out), or refuses with a message.</summary>
    private async Task<JsonNode?> CleanAsync(string app, FieldDef f, JsonNode? raw, CancellationToken ct)
    {
        string? s = raw is null ? null : raw is JsonValue jv && jv.TryGetValue<string>(out var str) ? str.Trim() : raw is JsonValue ? raw.ToString().Trim() : null;
        bool empty = raw is null || (raw is JsonValue && string.IsNullOrEmpty(s)) || (raw is JsonArray arr0 && arr0.Count == 0);
        if (empty && f.Default is not null && f.Type != "lines") { s = f.Default; empty = false; raw = JsonValue.Create(f.Default); }
        if (empty)
        {
            if (f.Required && f.Type != "bool") throw new BusinessRuleException($"{f.Label} is required.");
            return f.Type == "bool" ? JsonValue.Create(false) : null;
        }
        switch (f.Type)
        {
            case "number":
            case "money":
                if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) throw new BusinessRuleException($"{f.Label} must be a number.");
                if (Math.Abs(n) > 9_999_999_999m) throw new BusinessRuleException($"{f.Label} is too large.");
                return JsonValue.Create(f.Type == "money" ? Math.Round(n, 2) : n);
            case "date":
                if (!DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var dt)) throw new BusinessRuleException($"{f.Label} must be a date.");
                return JsonValue.Create(dt.ToString("yyyy-MM-dd"));
            case "bool":
                return JsonValue.Create(raw is JsonValue bv && (bv.TryGetValue<bool>(out var b) ? b : string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)));
            case "select":
                if (f.Options is { Length: > 0 } && !f.Options.Contains(s)) throw new BusinessRuleException($"Choose one of the {f.Label.ToLowerInvariant()} options.");
                return JsonValue.Create(s);
            case "email":
                if (!s!.Contains('@') || !s.Contains('.') || s.Any(char.IsWhiteSpace)) throw new BusinessRuleException($"{f.Label} isn't a valid email address.");
                return JsonValue.Create(s.Length > 150 ? s[..150] : s);
            case "lookup":
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) throw new BusinessRuleException($"Choose {f.Label.ToLowerInvariant()}.");
                var found = f.Lookup switch
                {
                    "customer" => await db.Customers.AnyAsync(x => x.Id == id, ct),
                    "employee" => await db.Employees.AnyAsync(x => x.Id == id, ct),
                    "product" => await db.Products.AnyAsync(x => x.Id == id, ct),
                    _ when f.Lookup?.StartsWith("record:") == true => await db.AppRecords.AnyAsync(x => x.Id == id && x.App == app && x.Kind == f.Lookup.Substring(7), ct),
                    _ => false,
                };
                if (!found) throw new BusinessRuleException($"{f.Label} no longer exists.");
                return JsonValue.Create(id);
            case "lines":
                if (raw is not JsonArray lines) throw new BusinessRuleException($"{f.Label} must be a list.");
                var clean = new JsonArray();
                foreach (var l in lines.OfType<JsonObject>())
                {
                    var pid = l.Int("productId"); var qty = l.Num("quantity");
                    if (pid is null or <= 0 || qty is null or <= 0 || qty != Math.Floor(qty.Value)) throw new BusinessRuleException($"Every line in {f.Label.ToLowerInvariant()} needs a product and a whole quantity above zero.");
                    var o = new JsonObject { ["productId"] = pid, ["quantity"] = (int)qty };
                    if (l.Num("price") is decimal price and >= 0) o["price"] = Math.Round(price, 2);
                    if (l.Str("note") is { Length: > 0 } note) o["note"] = note.Length > 200 ? note[..200] : note;
                    clean.Add(o);
                }
                if (clean.Count > 200) throw new BusinessRuleException($"{f.Label} has too many lines.");
                var pids = clean.OfType<JsonObject>().Select(o => o.Int("productId")!.Value).Distinct().ToList();
                if (await db.Products.CountAsync(p => pids.Contains(p.Id), ct) != pids.Count) throw new BusinessRuleException($"A product in {f.Label.ToLowerInvariant()} no longer exists.");
                return clean;
            case "customers":
                if (raw is not JsonArray picked) throw new BusinessRuleException($"{f.Label} must be a list.");
                var cids = picked.Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : int.TryParse(x?.ToString(), out var j) ? j : 0).Where(x => x > 0).Distinct().ToList();
                if (cids.Count > 2000) throw new BusinessRuleException($"Pick at most 2000 in {f.Label.ToLowerInvariant()}.");
                var known = await db.Customers.Where(x => cids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
                return new JsonArray(known.OrderBy(x => x).Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
            case "textarea":
                return JsonValue.Create(s!.Length > 4000 ? s[..4000] : s);
            case "json":
                // Structured data a custom screen edits (a spreadsheet's cells). Kept as sent, but bounded.
                if (raw.ToJsonString().Length > 400_000) throw new BusinessRuleException($"{f.Label} is too large.");
                return raw.DeepClone();
            default:
                var max = f.Key == "title" ? 200 : f.Key == "code" ? 80 : 300;
                return JsonValue.Create(s!.Length > max ? s[..max] : s);
        }
    }

    public async Task SetStatusAsync(string app, int id, string status, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app);
        var r = await db.AppRecords.SingleOrDefaultAsync(x => x.Id == id && x.App == app, ct) ?? throw new NotFoundException("Record");
        var k = KindOf(d, r.Kind);
        if (r.Status == status) return;
        var cur = k.Statuses.FirstOrDefault(s => s.Name == r.Status);
        if (!k.Statuses.Any(s => s.Name == status) || cur is null || !cur.Next.Contains(status)) throw new BusinessRuleException($"A {k.Label.ToLowerInvariant()} that is {r.Status.ToLowerInvariant()} can't go to {status.ToLowerInvariant()}.");
        if (NeedsApproval(user, app, status, null)) { await SubmitAsync(d, r, "status", status, user, ct); return; }
        var hook = Hook(app);
        if (hook is not null) await hook.BeforeStatusAsync(Ctx(d, user, baseUrl), r, status, ct);
        var from = r.Status; r.Status = status; r.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = $"{app.ToUpperInvariant()}_STATUS", Entity = "AppRecord", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{r.Title}: {from} → {status}" });
        await db.SaveChangesAsync(ct);
        if (hook is not null) { await hook.AfterStatusAsync(Ctx(d, user, baseUrl), r, from, ct); await db.SaveChangesAsync(ct); }
    }

    public async Task<ActionResultDto> ActionAsync(string app, int id, string action, string? text, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app);
        var r = await db.AppRecords.SingleOrDefaultAsync(x => x.Id == id && x.App == app, ct) ?? throw new NotFoundException("Record");
        var def = KindOf(d, r.Kind).Actions?.FirstOrDefault(a => a.Key == action) ?? throw new NotFoundException("Action");
        if (def.InStatuses is { Length: > 0 } && !def.InStatuses.Contains(r.Status)) throw new BusinessRuleException($"That can't be done while it is {r.Status.ToLowerInvariant()}.");
        var hook = Hook(app) ?? throw new BusinessRuleException("This action isn't available.");
        if (NeedsApproval(user, app, null, action))
        {
            // check it would work before bothering the admin (an empty audience, no message), then hand it over
            if (hook is BroadcastHookBase) await hook.ActionAsync(Ctx(d, user, baseUrl), r, "count", null, ct);
            if (string.IsNullOrWhiteSpace(Dx.Parse(r.Data).Str("message"))) throw new BusinessRuleException("Write the message first.");
            await SubmitAsync(d, r, "action", action, user, ct);
            return new ActionResultDto("Sent to an administrator for approval. It goes out as soon as they approve it, and you'll see it here.");
        }
        var result = await hook.ActionAsync(Ctx(d, user, baseUrl), r, action, text, ct);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = $"{app.ToUpperInvariant()}_{action.ToUpperInvariant()}", Entity = "AppRecord", EntityId = id.ToString(), At = clock.UtcNow, Detail = r.Title });
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task DeleteAsync(string app, int id, CurrentUser user, string baseUrl, CancellationToken ct = default)
    {
        var d = Def(app);
        var r = await db.AppRecords.SingleOrDefaultAsync(x => x.Id == id && x.App == app, ct) ?? throw new NotFoundException("Record");
        if (KindOf(d, r.Kind).NoDelete) throw new BusinessRuleException("This can't be deleted.");
        if (Hook(app) is { } h) await h.BeforeDeleteAsync(Ctx(d, user, baseUrl), r, ct);
        // Children go with their parent (a course's lessons, an event's registrations); the foreign key refuses a parent that still has children, so delete them first.
        var all = new List<AppRecord> { r }; var frontier = new List<int> { r.Id };
        while (frontier.Count > 0)
        {
            var kids = await db.AppRecords.Where(x => x.ParentId != null && frontier.Contains(x.ParentId.Value)).ToListAsync(ct);
            all.AddRange(kids); frontier = kids.Select(x => x.Id).ToList();
        }
        db.AppRecords.RemoveRange(all.AsEnumerable().Reverse());
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = $"{app.ToUpperInvariant()}_DELETED", Entity = "AppRecord", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{r.Title} (+{all.Count - 1})" });
        await db.SaveChangesAsync(ct);
    }
}
