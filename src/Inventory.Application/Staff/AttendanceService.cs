using Inventory.Application.Abstractions;
using Inventory.Application.Queries;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Staff;

public sealed record AttendanceToday(DateTime? FirstCheckInAt, DateTime? LastCheckOutAt, int CheckIns, DateTime? LastCheckInAt);
public sealed record AttendanceEventDto(long Id, string FullName, DateOnly WorkDate, DateTime At, string Event);
public sealed record AttendanceDayDto(int UserId, string FullName, DateOnly WorkDate, DateTime? FirstIn, DateTime? LastOut, int CheckIns);

/// <summary>
/// Clerk attendance as an append-only event log (ported from Attendance.vb). Every check-in, check-out and declined check-in
/// appends its own row stamped with the moment it happened: a clerk who signs in three times is checked in three times.
/// WorkDate is the Lagos calendar day of that instant, so a 23:59 check-in lands on the right day.
/// </summary>
public sealed class AttendanceService(IBusinessDbContext db, IClock clock)
{
    private DateTime Log(CurrentUser user, string type)
    {
        var now = clock.UtcNow;
        db.AttendanceEvents.Add(new AttendanceEvent { UserId = user.Id, FullName = user.FullName, EventType = type, HappenedAt = now, WorkDate = DateOnly.FromDateTime(clock.BusinessNow) });
        return now;
    }

    public async Task<DateTime> CheckInAsync(CurrentUser user, CancellationToken ct = default)
    {
        var at = Log(user, AttendanceEventTypes.In);
        await db.SaveChangesAsync(ct);
        return at;
    }

    /// <summary>Never throws: attendance logging must not block anyone signing out.</summary>
    public async Task CheckOutAsync(CurrentUser user, CancellationToken ct = default)
    {
        try { Log(user, AttendanceEventTypes.Out); await db.SaveChangesAsync(ct); } catch { db.ClearTracker(); }
    }

    /// <summary>She reached the welcome step and chose not to clock on. Logged so an admin can see she was here.</summary>
    public async Task DeclineAsync(CurrentUser user, CancellationToken ct = default)
    {
        try { Log(user, AttendanceEventTypes.Declined); await db.SaveChangesAsync(ct); } catch { db.ClearTracker(); }
    }

    public async Task<AttendanceToday> TodayAsync(int userId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.BusinessNow);
        var rows = await db.AttendanceEvents.AsNoTracking().Where(e => e.UserId == userId && e.WorkDate == today).Select(e => new { e.EventType, e.HappenedAt }).ToListAsync(ct);
        var ins = rows.Where(r => r.EventType == AttendanceEventTypes.In).Select(r => r.HappenedAt).ToList();
        var outs = rows.Where(r => r.EventType == AttendanceEventTypes.Out).Select(r => r.HappenedAt).ToList();
        return new AttendanceToday(ins.Count > 0 ? ins.Min() : null, outs.Count > 0 ? outs.Max() : null, ins.Count, ins.Count > 0 ? ins.Max() : null);
    }

    /// <summary>The audit trail: every event in the window, most recent first, never rolled up.</summary>
    public async Task<PagedResult<AttendanceEventDto>> EventsAsync(DateOnly from, DateOnly to, PageRequest page, CancellationToken ct = default)
    {
        var q = db.AttendanceEvents.AsNoTracking().Where(e => e.WorkDate >= from && e.WorkDate <= to);
        if (page.Term is { } t) q = q.Where(e => e.FullName.Contains(t));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.HappenedAt).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new PagedResult<AttendanceEventDto>(rows.Select(e => new AttendanceEventDto(e.Id, e.FullName, e.WorkDate, e.HappenedAt,
            e.EventType switch { AttendanceEventTypes.In => "Checked in", AttendanceEventTypes.Out => "Checked out", _ => "Declined to check in" })).ToList(), total, page.SafePage, page.SafeSize);
    }

    /// <summary>One line per person per day: first in, last out, number of check-ins. Grouped by user id, not name, so a corrected name doesn't split a day in two.</summary>
    public async Task<List<AttendanceDayDto>> DailySummaryAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.AttendanceEvents.AsNoTracking().Where(e => e.WorkDate >= from && e.WorkDate <= to).ToListAsync(ct);
        return rows.GroupBy(e => (e.UserId, e.WorkDate))
            .Select(g => new AttendanceDayDto(g.Key.UserId, g.OrderByDescending(e => e.HappenedAt).First().FullName, g.Key.WorkDate,
                g.Where(e => e.EventType == AttendanceEventTypes.In).Select(e => (DateTime?)e.HappenedAt).Min(),
                g.Where(e => e.EventType == AttendanceEventTypes.Out).Select(e => (DateTime?)e.HappenedAt).Max(),
                g.Count(e => e.EventType == AttendanceEventTypes.In)))
            .OrderByDescending(d => d.WorkDate).ThenBy(d => d.FullName).ToList();
    }
}
