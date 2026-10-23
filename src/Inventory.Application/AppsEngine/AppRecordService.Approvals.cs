using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

public sealed record ApprovalDto(string App, string AppName, int Id, string Kind, string Title, string What, string SubmittedBy, DateTime SubmittedAt,
    IReadOnlyList<ApprovalField> Content);
public sealed record ApprovalField(string Label, string Value);

/// <summary>
/// Marketing that goes in front of customers needs an admin's yes when a digital marketer puts it live: sending a broadcast, switching an automation rule on,
/// scheduling or posting a social post, publishing a blog post or opening a survey. The record waits as "Awaiting approval" with what was asked; approving
/// does exactly that thing in the admin's name, rejecting puts the record back with a note. Admins and managers are never held up.
/// </summary>
public sealed partial class AppRecordService
{
    public const string AwaitingApproval = "Awaiting approval";
    private const string PendingKey = "_approval", RejectedKey = "_rejected";

    /// <summary>The moves that put something in front of customers, per app.</summary>
    private static readonly Dictionary<string, (string[] Statuses, string[] Actions)> GoLive = new()
    {
        ["email-marketing"] = ([], ["send"]),
        ["sms-marketing"] = ([], ["send"]),
        ["whatsapp"] = ([], ["send"]),
        ["marketing-automation"] = (["On"], ["run"]),
        ["social-marketing"] = (["Scheduled", "Posted"], []),
        ["blog"] = (["Published"], []),
        ["surveys"] = (["Open"], []),
    };

    /// <summary>Someone whose role may prepare marketing but not put it live by themselves (no "Publish without approval" permission).</summary>
    public static bool NeedsApproval(CurrentUser u) => !u.Can(Permissions.MarketingPublish);

    private static bool NeedsApproval(CurrentUser user, string app, string? status, string? action) =>
        NeedsApproval(user) && GoLive.TryGetValue(app, out var g) && ((status is not null && g.Statuses.Contains(status)) || (action is not null && g.Actions.Contains(action)));

    private async Task SubmitAsync(AppDef d, AppRecord r, string kind, string value, CurrentUser user, CancellationToken ct)
    {
        var data = Dx.Parse(r.Data);
        data[PendingKey] = new JsonObject { ["kind"] = kind, ["value"] = value, ["from"] = r.Status, ["by"] = user.FullName, ["byId"] = user.Id, ["at"] = clock.UtcNow.ToString("O") };
        data.Remove(RejectedKey);
        r.Data = data.ToJsonString(); r.Status = AwaitingApproval; r.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "APPROVAL_REQUESTED", Entity = "AppRecord", EntityId = r.Id.ToString(), At = clock.UtcNow, Detail = $"{d.Name}: {r.Title} ({Describe(kind, value)})" });
        await db.SaveChangesAsync(ct);
        if (alerts is not null)
            await alerts.TellStaffAsync(new Partners.StaffAlert($"{company.LegalName}: approval needed — {d.Name}: {r.Title}",
                $"📝 APPROVAL NEEDED — {company.LegalName}\n{user.FullName} wants to {Describe(kind, value)}: \"{r.Title}\" ({d.Name}).\n{Preview(r)}\nApprove or reject in Inventory → Approvals.", AdminOnly: true), ct);
    }

    /// <summary>Everything waiting for an admin, newest first, with the words that would go out.</summary>
    public async Task<List<ApprovalDto>> ApprovalsAsync(CancellationToken ct)
    {
        var rows = await db.AppRecords.AsNoTracking().Where(r => r.Status == AwaitingApproval).OrderByDescending(r => r.UpdatedAt).Take(200).ToListAsync(ct);
        var list = new List<ApprovalDto>();
        foreach (var r in rows)
        {
            var d = Def(r.App); var k = KindOf(d, r.Kind); var data = Dx.Parse(r.Data);
            var p = data[PendingKey] as JsonObject;
            var fields = k.Fields.Where(f => f.Key != "title" && data[f.Key] is not null && f.Type is not ("customers" or "json" or "lines"))
                .Select(f => new ApprovalField(f.Label, data[f.Key]!.ToString())).Where(f => f.Value.Length > 0).ToList();
            if (data["customers"] is JsonArray picked) fields.Add(new ApprovalField("Selected customers", $"{picked.Count} customer(s)"));
            list.Add(new ApprovalDto(r.App, d.Name, r.Id, r.Kind, r.Title, Describe(p?.Str("kind") ?? "", p?.Str("value") ?? ""), p?.Str("by") ?? r.CreatedByName ?? "",
                DateTime.TryParse(p?.Str("at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : r.UpdatedAt, fields));
        }
        return list;
    }

    /// <summary>Does what the marketer asked, in the approving admin's name. If it can't be done (nobody reachable any more), the record goes back and the reason is shown.</summary>
    public async Task<ActionResultDto> ApproveAsync(string app, int id, CurrentUser admin, string baseUrl, CancellationToken ct)
    {
        if (!admin.Can(Permissions.MarketingApprove)) throw new ForbiddenActionException("Your role can't approve marketing.");
        var (d, r, kind, value) = await PendingAsync(app, id, ct);
        var data = Dx.Parse(r.Data); var from = (data[PendingKey] as JsonObject)?.Str("from") ?? KindOf(d, r.Kind).Statuses[0].Name;
        data.Remove(PendingKey); data["_approvedBy"] = admin.FullName;
        r.Data = data.ToJsonString(); r.Status = from; r.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = admin.Id, UserName = admin.FullName, Action = "APPROVED", Entity = "AppRecord", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{d.Name}: {r.Title} ({Describe(kind, value)})" });
        await db.SaveChangesAsync(ct);
        if (kind == "status") { await SetStatusAsync(app, id, value, admin, baseUrl, ct); return new ActionResultDto($"Approved: \"{r.Title}\" is now {value.ToLowerInvariant()}."); }
        return await ActionAsync(app, id, value, null, admin, baseUrl, ct);
    }

    public async Task RejectAsync(string app, int id, string? note, CurrentUser admin, CancellationToken ct)
    {
        if (!admin.Can(Permissions.MarketingApprove)) throw new ForbiddenActionException("Your role can't approve marketing.");
        var (d, r, kind, value) = await PendingAsync(app, id, ct);
        var data = Dx.Parse(r.Data); var from = (data[PendingKey] as JsonObject)?.Str("from") ?? KindOf(d, r.Kind).Statuses[0].Name;
        data.Remove(PendingKey);
        var why = string.IsNullOrWhiteSpace(note) ? "No reason given." : note.Trim().Length > 300 ? note.Trim()[..300] : note.Trim();
        data[RejectedKey] = new JsonObject { ["by"] = admin.FullName, ["at"] = clock.UtcNow.ToString("O"), ["note"] = why };
        r.Data = data.ToJsonString(); r.Status = from; r.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = admin.Id, UserName = admin.FullName, Action = "REJECTED", Entity = "AppRecord", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{d.Name}: {r.Title} ({Describe(kind, value)}): {why}" });
        await db.SaveChangesAsync(ct);
    }

    private async Task<(AppDef D, AppRecord R, string Kind, string Value)> PendingAsync(string app, int id, CancellationToken ct)
    {
        var d = Def(app);
        var r = await db.AppRecords.SingleOrDefaultAsync(x => x.Id == id && x.App == app, ct) ?? throw new NotFoundException("Record");
        if (r.Status != AwaitingApproval || Dx.Parse(r.Data)[PendingKey] is not JsonObject p) throw new BusinessRuleException("This isn't waiting for approval any more.");
        return (d, r, p.Str("kind") ?? "", p.Str("value") ?? "");
    }

    private static string Describe(string kind, string value) => (kind, value) switch
    {
        ("action", "send") => "send it to customers",
        ("action", "run") => "run the rule now",
        ("status", "On") => "switch the rule on",
        ("status", "Published") => "publish it",
        ("status", "Open") => "open it to customers",
        ("status", "Scheduled") => "schedule the post",
        ("status", "Posted") => "post it",
        _ => $"{kind} {value}".Trim(),
    };

    private static string Preview(AppRecord r)
    {
        var d = Dx.Parse(r.Data);
        var text = d.Str("message") ?? d.Str("content") ?? d.Str("summary") ?? d.Str("intro") ?? "";
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
