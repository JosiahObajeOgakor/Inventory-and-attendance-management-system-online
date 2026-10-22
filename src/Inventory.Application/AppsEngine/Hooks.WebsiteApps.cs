using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

/// <summary>Website: one set of site settings; each page gets its own web address.</summary>
public sealed class WebsiteHook : IAppHook
{
    public string App => "website";

    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind == "site")
        {
            if (isNew && await c.Db.AppRecords.AnyAsync(x => x.App == "website" && x.Kind == "site", ct)) throw new BusinessRuleException("The site already has its settings. Edit them instead.");
            var accent = data.Str("accent");
            if (!string.IsNullOrWhiteSpace(accent) && !System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9a-fA-F]{6}$")) throw new BusinessRuleException("The colour must look like #0f1e3d.");
            return;
        }
        await AppHelpers.EnsureCodeAsync(c.Db, r, ct);
    }
}

/// <summary>Events: tickets can't exceed the places; a registration's amount is tickets × the event's price.</summary>
public sealed class EventsHook : IAppHook
{
    public string App => "events";

    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind == "event")
        {
            await AppHelpers.EnsureCodeAsync(c.Db, r, ct);
            if ((data.Num("capacity") ?? 0) < 0 || (data.Num("price") ?? 0) < 0) throw new BusinessRuleException("Places and price can't be negative.");
            r.Amount = data.Num("price");
            return;
        }
        if (isNew) data.Set("source", "Entered by staff");
        await CheckAsync(c.Db, r, data.Int("tickets") ?? 1, ct);
        var ev = await c.Db.AppRecords.AsNoTracking().SingleAsync(x => x.Id == r.ParentId, ct);
        r.Amount = AppHelpers.Round((data.Int("tickets") ?? 1) * (Dx.Parse(ev.Data).Num("price") ?? 0));
        r.Date = ev.Date;
    }

    /// <summary>Refuses a registration that would go over the event's places (cancelled ones free theirs).</summary>
    public static async Task CheckAsync(Abstractions.IBusinessDbContext db, AppRecord reg, int tickets, CancellationToken ct)
    {
        if (tickets < 1 || tickets > 50) throw new BusinessRuleException("Book between 1 and 50 tickets.");
        var ev = await db.AppRecords.AsNoTracking().SingleAsync(x => x.Id == reg.ParentId, ct);
        var capacity = Dx.Parse(ev.Data).Int("capacity") ?? 0;
        if (capacity <= 0) return;
        var taken = (await db.AppRecords.AsNoTracking().Where(x => x.App == "events" && x.Kind == "registration" && x.ParentId == reg.ParentId && x.Id != reg.Id && x.Status != "Cancelled").Select(x => x.Data).ToListAsync(ct))
            .Sum(d => Dx.Parse(d).Int("tickets") ?? 1);
        if (taken + tickets > capacity) throw new BusinessRuleException(capacity - taken <= 0 ? "This event is full." : $"Only {capacity - taken} place(s) left.");
    }

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var upcoming = await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "events" && x.Kind == "event" && x.Status == "Published" && x.Date >= c.Today).Select(x => x.Id).ToListAsync(ct);
        var regs = (await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "events" && x.Kind == "registration" && x.Status != "Cancelled" && upcoming.Contains(x.ParentId!.Value)).Select(x => x.Data).ToListAsync(ct))
            .Sum(d => Dx.Parse(d).Int("tickets") ?? 1);
        return [new Card("Upcoming events", upcoming.Count.ToString()), new Card("Tickets booked for them", regs.ToString())];
    }
}

/// <summary>Blog: a web address from the headline; a post published without a date is dated today.</summary>
public sealed class BlogHook : IAppHook
{
    public string App => "blog";
    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        await AppHelpers.EnsureCodeAsync(c.Db, r, ct);
        r.Date ??= c.Today;
    }
}

/// <summary>eLearning: web addresses for courses; each student gets a private link; paid courses unlock when payment is ticked.</summary>
public sealed class ElearningHook : IAppHook
{
    public string App => "elearning";

    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        switch (r.Kind)
        {
            case "course": await AppHelpers.EnsureCodeAsync(c.Db, r, ct); r.Amount = data.Num("price"); break;
            case "lesson": r.Amount = null; break;
            case "enrollment":
                var email = (data.Str("email") ?? "").ToLowerInvariant();
                data.Set("email", email);
                if (await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "elearning" && x.Kind == "enrollment" && x.ParentId == r.ParentId && x.Id != r.Id).Select(x => x.Data).ToListAsync(ct) is { } all
                    && all.Any(d => string.Equals(Dx.Parse(d).Str("email"), email, StringComparison.OrdinalIgnoreCase)))
                    throw new BusinessRuleException("That email is already enrolled in this course.");
                if (isNew) { r.Code = Guid.NewGuid().ToString("N"); data.Set("done", 0); }
                break;
        }
    }

    public Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct) =>
        Task.FromResult(new ActionResultDto("Send the student this link. It is theirs alone: it opens their lessons and keeps their progress.", $"{c.BaseUrl}/learn/{r.Code}"));

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var students = await c.Db.AppRecords.AsNoTracking().CountAsync(x => x.App == "elearning" && x.Kind == "enrollment", ct);
        var done = await c.Db.AppRecords.AsNoTracking().CountAsync(x => x.App == "elearning" && x.Kind == "enrollment" && x.Status == "Completed", ct);
        return [new Card("Students", students.ToString()), new Card("Finished a course", done.ToString(), null, "ok")];
    }
}

/// <summary>Forum: a reply is titled by its first words.</summary>
public sealed class ForumHook : IAppHook
{
    public string App => "forum";
    public Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind == "reply" && string.IsNullOrWhiteSpace(r.Title)) { var b = data.Str("body") ?? "Reply"; r.Title = b.Length > 60 ? b[..60] + "…" : b; }
        return Task.CompletedTask;
    }
}
