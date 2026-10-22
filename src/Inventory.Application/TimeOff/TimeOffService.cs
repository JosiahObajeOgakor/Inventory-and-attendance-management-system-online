using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.TimeOff;

public sealed record LeaveTypeDto(int Id, string Name, int DaysPerYear, bool Paid, bool IsActive);
public sealed record LeaveTypeInput(int? Id, string Name, int DaysPerYear, bool Paid, bool IsActive);
public sealed record LeaveRequestDto(int Id, int EmployeeId, string Employee, int LeaveTypeId, string LeaveType, bool Paid, DateOnly FromDate, DateOnly ToDate, int Days, string? Reason,
    string Status, string? RequestedBy, string? DecidedBy, DateTime? DecidedAt, string? DecisionNote, DateTime CreatedAt);
public sealed record LeaveRequestInput(int EmployeeId, int LeaveTypeId, DateOnly FromDate, DateOnly ToDate, string? Reason);
public sealed record DecisionInput(bool Approve, string? Note);
/// <param name="Entitled">0 = no limit.</param>
public sealed record BalanceRow(int EmployeeId, string Employee, int LeaveTypeId, string LeaveType, int Entitled, int Taken, int Pending, int? Left);
public sealed record AwayDto(int EmployeeId, string Employee, string LeaveType, DateOnly ToDate);
public sealed record TimeOffDto(int Year, IReadOnlyList<LeaveTypeDto> Types, IReadOnlyList<LeaveRequestDto> Requests, IReadOnlyList<BalanceRow> Balances, IReadOnlyList<AwayDto> AwayToday, int PendingCount);
public sealed record UnpaidRow(int EmployeeId, string Employee, int Days);

/// <summary>
/// Time Off: leave types with a yearly allowance, requests recorded for employees, approval by someone other than the person who recorded it, and
/// balances that count approved days against the allowance. Working days are Monday to Friday. Unpaid leave is reported per month for payroll.
/// </summary>
public sealed class TimeOffService(IBusinessDbContext db, IClock clock)
{
    /// <summary>Starting leave types, created the first time Time Off is opened. They are ordinary rows the business can edit.</summary>
    private static readonly (string Name, int Days, bool Paid)[] Defaults =
        [("Annual leave", 15, true), ("Sick leave", 12, true), ("Compassionate leave", 5, true), ("Maternity leave", 60, true), ("Paternity leave", 10, true), ("Unpaid leave", 0, false)];

    public static int WorkingDays(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1)) if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        return n;
    }

    private async Task EnsureTypesAsync(CancellationToken ct)
    {
        if (await db.LeaveTypes.AnyAsync(ct)) return;
        foreach (var (name, days, paid) in Defaults) db.LeaveTypes.Add(new LeaveType { Name = name, DaysPerYear = days, Paid = paid });
        await db.SaveChangesAsync(ct);
    }

    public async Task<TimeOffDto> OverviewAsync(int? year, string? status, CancellationToken ct = default)
    {
        await EnsureTypesAsync(ct);
        var y = year ?? clock.BusinessToday.Year;
        var today = clock.BusinessToday;
        var types = await db.LeaveTypes.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct);
        var staff = await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync(ct);
        var names = (await db.Employees.AsNoTracking().Select(e => new { e.Id, e.FullName }).ToListAsync(ct)).ToDictionary(e => e.Id, e => e.FullName);
        var all = await db.LeaveRequests.AsNoTracking().Where(r => r.FromDate.Year == y || r.ToDate.Year == y).OrderByDescending(r => r.FromDate).ToListAsync(ct);
        var typeBy = types.ToDictionary(t => t.Id);

        var shown = all.Where(r => string.IsNullOrEmpty(status) || r.Status == status).Select(r => Dto(r, names, typeBy)).ToList();
        var balances = new List<BalanceRow>();
        foreach (var t in types.Where(t => t.IsActive && t.DaysPerYear > 0))
            foreach (var e in staff)
            {
                var mine = all.Where(r => r.EmployeeId == e.Id && r.LeaveTypeId == t.Id && r.FromDate.Year == y);
                var taken = mine.Where(r => r.Status == LeaveStatuses.Approved).Sum(r => r.Days);
                var pending = mine.Where(r => r.Status == LeaveStatuses.Pending).Sum(r => r.Days);
                balances.Add(new BalanceRow(e.Id, e.FullName, t.Id, t.Name, t.DaysPerYear, taken, pending, Math.Max(0, t.DaysPerYear - taken - pending)));
            }
        var away = all.Where(r => r.Status == LeaveStatuses.Approved && r.FromDate <= today && r.ToDate >= today)
            .Select(r => new AwayDto(r.EmployeeId, names.GetValueOrDefault(r.EmployeeId, "Employee"), typeBy[r.LeaveTypeId].Name, r.ToDate)).ToList();
        return new TimeOffDto(y, types.Select(t => new LeaveTypeDto(t.Id, t.Name, t.DaysPerYear, t.Paid, t.IsActive)).ToList(), shown, balances, away,
            all.Count(r => r.Status == LeaveStatuses.Pending));
    }

    private static LeaveRequestDto Dto(LeaveRequest r, Dictionary<int, string> names, Dictionary<int, LeaveType> types) =>
        new(r.Id, r.EmployeeId, names.GetValueOrDefault(r.EmployeeId, "Employee"), r.LeaveTypeId, types[r.LeaveTypeId].Name, types[r.LeaveTypeId].Paid, r.FromDate, r.ToDate, r.Days, r.Reason,
            r.Status, r.RequestedByName, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.CreatedAt);

    public async Task<int> SaveTypeAsync(LeaveTypeInput i, CurrentUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Name the leave type.");
        if (i.DaysPerYear is < 0 or > 365) throw new BusinessRuleException("Days a year must be between 0 and 365.");
        var name = i.Name.Trim();
        if (await db.LeaveTypes.AnyAsync(t => t.Name == name && t.Id != (i.Id ?? 0), ct)) throw new BusinessRuleException("There is already a leave type with that name.");
        var t = i.Id is int id ? await db.LeaveTypes.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Leave type") : db.LeaveTypes.Add(new LeaveType()).Entity;
        t.Name = name; t.DaysPerYear = i.DaysPerYear; t.Paid = i.Paid; t.IsActive = i.IsActive;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "LEAVE_TYPE_SAVED", Entity = "LeaveType", At = clock.UtcNow, Detail = $"{name}: {i.DaysPerYear} day(s)" });
        await db.SaveChangesAsync(ct);
        return t.Id;
    }

    /// <summary>Days of this type already used or waiting for approval in the year of <paramref name="from"/>, for the allowance check.</summary>
    private async Task<int> CommittedAsync(int employeeId, int typeId, int year, int? exceptRequest, CancellationToken ct) =>
        await db.LeaveRequests.Where(r => r.EmployeeId == employeeId && r.LeaveTypeId == typeId && r.FromDate.Year == year && r.Id != (exceptRequest ?? 0)
                                          && (r.Status == LeaveStatuses.Approved || r.Status == LeaveStatuses.Pending)).SumAsync(r => (int?)r.Days, ct) ?? 0;

    public async Task<int> RequestAsync(LeaveRequestInput i, CurrentUser user, CancellationToken ct = default)
    {
        if (i.ToDate < i.FromDate) throw new BusinessRuleException("The last day can't be before the first day.");
        if (i.FromDate.Year != i.ToDate.Year) throw new BusinessRuleException("Split leave that crosses into a new year into two requests, so each year's allowance is counted correctly.");
        var emp = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == i.EmployeeId && e.IsActive, ct) ?? throw new NotFoundException("Employee");
        var type = await db.LeaveTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == i.LeaveTypeId && t.IsActive, ct) ?? throw new NotFoundException("Leave type");
        var days = WorkingDays(i.FromDate, i.ToDate);
        if (days == 0) throw new BusinessRuleException("Those dates are all weekend. Choose working days.");
        if (await db.LeaveRequests.AnyAsync(r => r.EmployeeId == emp.Id && (r.Status == LeaveStatuses.Approved || r.Status == LeaveStatuses.Pending) && r.FromDate <= i.ToDate && r.ToDate >= i.FromDate, ct))
            throw new BusinessRuleException($"{emp.FullName} already has leave that overlaps those dates.");
        if (type.DaysPerYear > 0)
        {
            var left = type.DaysPerYear - await CommittedAsync(emp.Id, type.Id, i.FromDate.Year, null, ct);
            if (days > left) throw new BusinessRuleException($"{emp.FullName} has {Math.Max(0, left)} day(s) of {type.Name} left in {i.FromDate.Year}, and this asks for {days}.");
        }
        var now = clock.UtcNow;
        var r = new LeaveRequest { EmployeeId = emp.Id, LeaveTypeId = type.Id, FromDate = i.FromDate, ToDate = i.ToDate, Days = days, Reason = i.Reason?.Trim() is { Length: > 0 } s ? s[..Math.Min(300, s.Length)] : null,
            RequestedByUserId = user.Id, RequestedByName = user.FullName, CreatedAt = now };
        db.LeaveRequests.Add(r);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "LEAVE_REQUESTED", Entity = "LeaveRequest", At = now, Detail = $"{emp.FullName}: {type.Name}, {days} day(s) from {i.FromDate:yyyy-MM-dd}" });
        await db.SaveChangesAsync(ct);
        return r.Id;
    }

    /// <summary>Approve or reject a pending request. The person who recorded it can't decide it.</summary>
    public async Task DecideAsync(int id, DecisionInput i, CurrentUser user, CancellationToken ct = default)
    {
        var r = await db.LeaveRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Leave request");
        if (r.Status != LeaveStatuses.Pending) throw new BusinessRuleException($"This request is already {r.Status.ToLowerInvariant()}.");
        if (r.RequestedByUserId == user.Id) throw new BusinessRuleException("Someone else has to approve a request you recorded.");
        if (!i.Approve && string.IsNullOrWhiteSpace(i.Note)) throw new BusinessRuleException("Say why the request is rejected.");
        if (i.Approve)
        {
            var type = await db.LeaveTypes.AsNoTracking().SingleAsync(t => t.Id == r.LeaveTypeId, ct);
            if (type.DaysPerYear > 0 && r.Days + await CommittedAsync(r.EmployeeId, r.LeaveTypeId, r.FromDate.Year, r.Id, ct) > type.DaysPerYear)
                throw new BusinessRuleException("The allowance for this leave type has been used up since the request was recorded.");
        }
        r.Status = i.Approve ? LeaveStatuses.Approved : LeaveStatuses.Rejected;
        r.DecidedByUserId = user.Id; r.DecidedByName = user.FullName; r.DecidedAt = clock.UtcNow;
        r.DecisionNote = i.Note?.Trim() is { Length: > 0 } n ? n[..Math.Min(300, n.Length)] : null;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = i.Approve ? "LEAVE_APPROVED" : "LEAVE_REJECTED", Entity = "LeaveRequest", EntityId = id.ToString(), At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Withdraws a pending request, or a future approved one.</summary>
    public async Task CancelAsync(int id, CurrentUser user, CancellationToken ct = default)
    {
        var r = await db.LeaveRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Leave request");
        if (r.Status == LeaveStatuses.Pending || (r.Status == LeaveStatuses.Approved && r.FromDate > clock.BusinessToday)) r.Status = LeaveStatuses.Cancelled;
        else throw new BusinessRuleException("Only a waiting request, or leave that hasn't started, can be cancelled.");
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "LEAVE_CANCELLED", Entity = "LeaveRequest", EntityId = id.ToString(), At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Approved unpaid-leave working days falling in a month, per employee: what payroll deducts.</summary>
    public async Task<IReadOnlyList<UnpaidRow>> UnpaidAsync(int year, int month, CancellationToken ct = default)
    {
        var first = new DateOnly(year, month, 1); var last = first.AddMonths(1).AddDays(-1);
        var rows = await (from r in db.LeaveRequests.AsNoTracking() join t in db.LeaveTypes on r.LeaveTypeId equals t.Id join e in db.Employees on r.EmployeeId equals e.Id
                          where !t.Paid && r.Status == LeaveStatuses.Approved && r.FromDate <= last && r.ToDate >= first
                          select new { r.EmployeeId, e.FullName, r.FromDate, r.ToDate }).ToListAsync(ct);
        return rows.GroupBy(x => new { x.EmployeeId, x.FullName })
            .Select(g => new UnpaidRow(g.Key.EmployeeId, g.Key.FullName, g.Sum(x => WorkingDays(x.FromDate < first ? first : x.FromDate, x.ToDate > last ? last : x.ToDate))))
            .Where(x => x.Days > 0).OrderBy(x => x.Employee).ToList();
    }
}
