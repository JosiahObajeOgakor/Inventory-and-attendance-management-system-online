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

/// <summary>Recruitment: hiring an applicant creates the employee record and links the two.</summary>
public sealed class RecruitmentHook : IAppHook
{
    public string App => "recruitment";

    public Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind == "job" && (data.Int("openings") ?? 1) < 1) throw new BusinessRuleException("A job needs at least one opening.");
        return Task.CompletedTask;
    }

    public async Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var salary) || salary < 0) throw new BusinessRuleException("Enter the monthly salary as a number.");
        var d = Dx.Parse(r.Data);
        if (d.Int("employeeId") is not null) throw new BusinessRuleException("This person is already an employee.");
        var job = await c.Db.AppRecords.AsNoTracking().SingleAsync(x => x.Id == r.ParentId, ct);
        var emp = new Employee { FullName = r.Title, Position = job.Title.Length > 100 ? job.Title[..100] : job.Title, Phone = d.Str("phone"), StartedOn = c.Today, MonthlySalary = AppHelpers.Round(salary), IsActive = true };
        c.Db.Employees.Add(emp);
        await c.Db.SaveChangesAsync(ct);
        d.Set("employeeId", emp.Id);
        r.Data = d.ToJsonString(); r.Status = "Hired"; r.UpdatedAt = c.Clock.UtcNow;
        return new ActionResultDto($"{r.Title} is now an employee ({job.Title}, {AppHelpers.Naira(salary)} a month). Complete their payroll details under People.");
    }

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var open = await c.Db.AppRecords.AsNoTracking().CountAsync(x => x.App == "recruitment" && x.Kind == "job" && x.Status == "Open", ct);
        var live = await c.Db.AppRecords.AsNoTracking().CountAsync(x => x.App == "recruitment" && x.Kind == "applicant" && (x.Status == "Applied" || x.Status == "Screening" || x.Status == "Interview" || x.Status == "Offer"), ct);
        return [new Card("Open jobs", open.ToString()), new Card("Applicants in the process", live.ToString())];
    }
}

/// <summary>Appraisals: four 1–5 scores; the overall score is their average.</summary>
public sealed class AppraisalsHook : IAppHook
{
    public string App => "appraisals";
    private static readonly string[] Scores = ["quality", "teamwork", "punctuality", "initiative"];

    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind != "review") return;
        if (!isNew && r.Status == "Acknowledged") throw new BusinessRuleException("An acknowledged review can't be changed.");
        foreach (var s in Scores)
            if (data.Num(s) is not decimal v || v < 1 || v > 5) throw new BusinessRuleException("Each score must be between 1 and 5.");
        var overall = AppHelpers.Round(Scores.Average(s => data.Num(s)!.Value));
        data.Set("overall", overall); r.Amount = overall;
        if (string.IsNullOrWhiteSpace(r.Title) && data.Int("employeeId") is int e)
            r.Title = await c.Db.Employees.AsNoTracking().Where(x => x.Id == e).Select(x => x.FullName).FirstOrDefaultAsync(ct) ?? "Review";
        r.Date ??= c.Today;
    }

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var scores = await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "appraisals" && x.Kind == "review" && x.Amount != null).Select(x => x.Amount!.Value).ToListAsync(ct);
        return [new Card("Average score", scores.Count == 0 ? "—" : AppHelpers.Round(scores.Average()).ToString("0.0", CultureInfo.InvariantCulture), $"{scores.Count} review(s)")];
    }
}

/// <summary>Fleet: papers expiring soon are flagged; a log entry with a higher odometer updates the vehicle's.</summary>
public sealed class FleetHook : IAppHook
{
    public string App => "fleet";

    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind != "log") return;
        if ((data.Num("amount") ?? 0) < 0) throw new BusinessRuleException("The cost can't be negative.");
        if (data.Num("odometer") is decimal km && r.ParentId is int vid && await c.Db.AppRecords.SingleOrDefaultAsync(x => x.Id == vid, ct) is { } v)
        {
            var vd = Dx.Parse(v.Data);
            if (km > (vd.Num("odometer") ?? 0)) { vd.Set("odometer", km); v.Data = vd.ToJsonString(); v.UpdatedAt = c.Clock.UtcNow; }
        }
    }

    private static readonly (string Key, string Label)[] Papers = [("insuranceExpiry", "Insurance"), ("roadworthyExpiry", "Road worthiness"), ("licenseExpiry", "Vehicle licence")];

    public IReadOnlyList<string> Flags(HookContext c, AppRecord r, JsonObject data) =>
        r.Kind != "vehicle" || r.Status == "Retired" ? [] : Papers.Select(p => AppHelpers.DueNote(p.Label, data.Date(p.Key), c.Today, 30)).OfType<string>().ToList();

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var vehicles = await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "fleet" && x.Kind == "vehicle" && x.Status != "Retired").Select(x => x.Data).ToListAsync(ct);
        var expiring = vehicles.Count(v => { var d = Dx.Parse(v); return Papers.Any(p => d.Date(p.Key) is DateOnly e && e.DayNumber - c.Today.DayNumber <= 30); });
        var monthStart = new DateOnly(c.Today.Year, c.Today.Month, 1);
        var spend = await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "fleet" && x.Kind == "log" && x.Date >= monthStart).SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        return [new Card("Vehicles with papers expiring or expired", expiring.ToString(), "within 30 days", expiring > 0 ? "warn" : "ok"), new Card("Spent this month", AppHelpers.Naira(spend))];
    }
}
