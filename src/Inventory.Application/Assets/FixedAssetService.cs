using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Ledger;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Assets;

public sealed record AssetDto(int Id, string Code, string Name, string Category, string? SerialNumber, string? Location, int? BranchId, DateOnly AcquiredOn, decimal Cost,
    decimal ResidualValue, string Method, int UsefulLifeMonths, decimal RatePct, decimal AccumulatedDepreciation, decimal BookValue, decimal MonthlyCharge, string Status,
    DateOnly? DisposedOn, decimal? DisposalProceeds, decimal? DisposalGainLoss);
/// <param name="PaidFrom">Bank, Cash, or Credit (owed to <paramref name="SupplierId"/>). Opening = owned before the system started (no payment).</param>
/// <param name="OpeningAccumulated">For an Opening asset: depreciation already charged before the system started.</param>
public sealed record NewAssetInput(string Name, string Category, string? SerialNumber, string? Location, int? BranchId, DateOnly AcquiredOn, decimal Cost, decimal ResidualValue,
    string Method, int UsefulLifeMonths, decimal RatePct, string PaidFrom, int? SupplierId, decimal OpeningAccumulated = 0);
public sealed record DisposeInput(DateOnly DisposedOn, decimal Proceeds, string ReceivedInto, string? Note);
public sealed record DepreciationPreview(int Year, int Month, bool AlreadyRun, decimal Total, IReadOnlyList<(int AssetId, string Code, string Name, decimal Amount)> Lines);
public sealed record DepreciationRunDto(int Id, int Year, int Month, decimal Total, int Assets, DateTime RunAt);
public sealed record AssetRegisterDto(IReadOnlyList<AssetDto> Assets, decimal TotalCost, decimal TotalAccumulated, decimal TotalBookValue,
    IReadOnlyList<(string Category, int Count, decimal Cost, decimal BookValue)> ByCategory, IReadOnlyList<DepreciationRunDto> Runs);

/// <summary>
/// The fixed-asset register (IAS 16 in outline): buying an asset capitalises it, a monthly run charges depreciation once per month (from the month
/// after it was bought), and a disposal removes its cost and depreciation and books the gain or loss.
/// </summary>
public sealed class FixedAssetService(IBusinessDbContext db, TransactionRunner tx, IClock clock, GeneralLedger gl)
{
    public static readonly string[] Categories =
        ["Land", "Buildings", "Plant and machinery", "Motor vehicles", "Furniture and fittings", "Computer equipment", "Office equipment", "Leasehold improvements"];

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static int MonthIndex(int y, int m) => y * 12 + m - 1;

    /// <summary>The charge for one month, never taking the asset below its residual value.</summary>
    public static decimal MonthlyCharge(FixedAsset a)
    {
        var room = a.Cost - a.ResidualValue - a.AccumulatedDepreciation;
        if (room <= 0 || a.Status == AssetStatuses.Disposed) return 0;
        var charge = a.Method switch
        {
            DepreciationMethods.StraightLine when a.UsefulLifeMonths > 0 => R((a.Cost - a.ResidualValue) / a.UsefulLifeMonths),
            DepreciationMethods.ReducingBalance => R(a.BookValue * a.RatePct / 100m / 12m),
            _ => 0,
        };
        return Math.Min(charge, room);
    }

    private static AssetDto Dto(FixedAsset a) => new(a.Id, a.Code, a.Name, a.Category, a.SerialNumber, a.Location, a.BranchId, a.AcquiredOn, a.Cost, a.ResidualValue, a.Method,
        a.UsefulLifeMonths, a.RatePct, a.AccumulatedDepreciation, a.BookValue, MonthlyCharge(a), a.Status, a.DisposedOn, a.DisposalProceeds, a.DisposalGainLoss);

    public async Task<AssetRegisterDto> RegisterAsync(CancellationToken ct = default)
    {
        var assets = await db.FixedAssets.AsNoTracking().OrderBy(a => a.Status == AssetStatuses.Disposed).ThenBy(a => a.Code).ToListAsync(ct);
        var live = assets.Where(a => a.Status != AssetStatuses.Disposed).ToList();
        var runs = await db.DepreciationRuns.AsNoTracking().OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(24)
            .Select(r => new DepreciationRunDto(r.Id, r.Year, r.Month, r.Total, r.Assets, r.RunAt)).ToListAsync(ct);
        return new AssetRegisterDto(assets.Select(Dto).ToList(), live.Sum(a => a.Cost), live.Sum(a => a.AccumulatedDepreciation), live.Sum(a => a.BookValue),
            live.GroupBy(a => a.Category).OrderBy(g => g.Key).Select(g => (g.Key, g.Count(), g.Sum(a => a.Cost), g.Sum(a => a.BookValue))).ToList(), runs);
    }

    public async Task<int> AddAsync(NewAssetInput i, CurrentUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Name the asset.");
        if (!Categories.Contains(i.Category)) throw new BusinessRuleException("Choose one of the asset categories.");
        if (i.Cost <= 0) throw new BusinessRuleException("Enter what the asset cost.");
        if (i.ResidualValue < 0 || i.ResidualValue >= i.Cost) throw new BusinessRuleException("The residual value must be less than the cost.");
        var method = i.Category == "Land" ? DepreciationMethods.None : i.Method;
        if (method == DepreciationMethods.StraightLine && i.UsefulLifeMonths is < 1 or > 1200) throw new BusinessRuleException("Enter the useful life in months (e.g. 48 for four years).");
        if (method == DepreciationMethods.ReducingBalance && i.RatePct is <= 0 or > 100) throw new BusinessRuleException("Enter the yearly reducing-balance rate.");
        if (method is not (DepreciationMethods.StraightLine or DepreciationMethods.ReducingBalance or DepreciationMethods.None)) throw new BusinessRuleException("Choose a depreciation method.");
        if (i.PaidFrom is not ("Bank" or "Cash" or "Credit" or "Opening")) throw new BusinessRuleException("Say how it was paid for.");
        if (i.PaidFrom == "Credit" && i.SupplierId is null) throw new BusinessRuleException("Choose the supplier it is owed to.");
        if (i.OpeningAccumulated < 0 || i.OpeningAccumulated > i.Cost - i.ResidualValue) throw new BusinessRuleException("Depreciation already charged can't exceed the cost less residual value.");
        if (i.PaidFrom != "Opening" && i.OpeningAccumulated != 0) throw new BusinessRuleException("Only an asset owned before the system started has depreciation brought forward.");

        return await tx.RunAsync(async inner =>
        {
            var supplier = i.SupplierId is int sid ? await db.Suppliers.FromSqlInterpolated($"SELECT * FROM suppliers WHERE Id = {sid} FOR UPDATE").SingleOrDefaultAsync(inner)
                                                     ?? throw new NotFoundException("Supplier") : null;
            var n = await db.FixedAssets.CountAsync(inner) + 1;
            var a = new FixedAsset
            {
                Code = $"FA-{n:00000}", Name = i.Name.Trim(), Category = i.Category, SerialNumber = string.IsNullOrWhiteSpace(i.SerialNumber) ? null : i.SerialNumber.Trim(),
                Location = string.IsNullOrWhiteSpace(i.Location) ? null : i.Location.Trim(), BranchId = i.BranchId, SupplierId = supplier?.Id, AcquiredOn = i.AcquiredOn,
                Cost = R(i.Cost), ResidualValue = R(i.ResidualValue), Method = method, UsefulLifeMonths = i.UsefulLifeMonths, RatePct = i.RatePct,
                AccumulatedDepreciation = R(i.OpeningAccumulated), CreatedByUserId = user.Id, CreatedAt = clock.UtcNow,
            };
            db.FixedAssets.Add(a);
            await db.SaveChangesAsync(inner);
            gl.Branch = a.BranchId ?? user.BranchId;
            var lines = new List<PostLine> { PostLine.Dr(GlCodes.FixedAssets, a.Cost) };
            switch (i.PaidFrom)
            {
                case "Bank": lines.Add(PostLine.Cr(GlCodes.Bank, a.Cost)); break;
                case "Cash": lines.Add(PostLine.Cr(GlCodes.Cash, a.Cost)); break;
                case "Credit":
                    lines.Add(PostLine.Cr(GlCodes.Payables, a.Cost, supplierId: supplier!.Id));
                    supplier.Balance += a.Cost;
                    db.Ledger.Add(new LedgerEntry { EntryDate = a.AcquiredOn, AccountType = LedgerAccountTypes.Supplier, AccountName = supplier.Name, SupplierId = supplier.Id,
                        EntryType = LedgerEntryTypes.Credit, Amount = a.Cost, Reference = a.Code });
                    break;
                default:   // owned before the system started: brought in at book value against owner's equity
                    lines.Add(PostLine.Cr(GlCodes.AccumulatedDepreciation, a.AccumulatedDepreciation));
                    lines.Add(PostLine.Cr(GlCodes.OwnersEquity, a.Cost - a.AccumulatedDepreciation));
                    break;
            }
            await gl.PostAsync("FixedAsset", a.Id.ToString(), "acquired", a.AcquiredOn, $"{a.Code} {a.Name}", lines, user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "ASSET_ADDED", Entity = "FixedAsset", EntityId = a.Id.ToString(), At = clock.UtcNow,
                Detail = $"{a.Code} {a.Name} ₦{a.Cost:N2} ({i.PaidFrom})" });
            await db.SaveChangesAsync(inner);
            return a.Id;
        }, ct);
    }

    /// <summary>What a month's run would charge: assets bought before that month, not disposed, not yet depreciated for it.</summary>
    public async Task<DepreciationPreview> PreviewAsync(int year, int month, CancellationToken ct = default)
    {
        var (assets, already) = await DueAsync(year, month, ct);
        var lines = assets.Select(a => (a.Id, a.Code, a.Name, MonthlyCharge(a))).Where(x => x.Item4 > 0).ToList();
        return new DepreciationPreview(year, month, already, lines.Sum(x => x.Item4), lines);
    }

    private async Task<(List<FixedAsset> Assets, bool AlreadyRun)> DueAsync(int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12) throw new BusinessRuleException("Choose a valid month.");
        var already = await db.DepreciationRuns.AnyAsync(r => r.Year == year && r.Month == month, ct);
        var first = new DateOnly(year, month, 1);
        var done = db.AssetDepreciations.Where(d => d.Year == year && d.Month == month).Select(d => d.AssetId);
        var assets = await db.FixedAssets.Where(a => a.Status == AssetStatuses.Active && a.AcquiredOn < first && !done.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync(ct);
        return (assets, already);
    }

    /// <summary>Charges a month's depreciation: one journal (Dr depreciation expense, Cr accumulated depreciation), each asset at most once.</summary>
    public Task<int> RunAsync(int year, int month, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            if (last > clock.BusinessToday.AddMonths(1)) throw new BusinessRuleException("You can't run depreciation for a month that hasn't started.");
            var (assets, _) = await DueAsync(year, month, inner);
            var charges = assets.Select(a => (a, amount: MonthlyCharge(a))).Where(x => x.amount > 0).ToList();
            if (charges.Count == 0) throw new BusinessRuleException("Nothing to depreciate for that month.");
            var run = new DepreciationRun { Year = year, Month = month, Total = charges.Sum(x => x.amount), Assets = charges.Count, RunByUserId = user.Id, RunAt = clock.UtcNow };
            db.DepreciationRuns.Add(run);
            await db.SaveChangesAsync(inner);
            foreach (var (a, amount) in charges)
            {
                a.AccumulatedDepreciation += amount;
                if (a.Cost - a.ResidualValue - a.AccumulatedDepreciation <= 0) a.Status = AssetStatuses.FullyDepreciated;
                db.AssetDepreciations.Add(new AssetDepreciation { AssetId = a.Id, Year = year, Month = month, Amount = amount, RunId = run.Id });
            }
            // One journal per branch, so each branch's profit carries its own assets' depreciation.
            foreach (var g in charges.GroupBy(x => x.a.BranchId))
            {
                gl.Branch = g.Key;
                var total = g.Sum(x => x.amount);
                await gl.PostAsync("Depreciation", run.Id.ToString(), $"branch:{g.Key?.ToString() ?? "none"}", last, $"Depreciation {last:MMMM yyyy} ({g.Count()} asset(s))",
                    [PostLine.Dr(GlCodes.Depreciation, total), PostLine.Cr(GlCodes.AccumulatedDepreciation, total)], user, inner);
            }
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "DEPRECIATION_RUN", Entity = "DepreciationRun", EntityId = run.Id.ToString(), At = clock.UtcNow,
                Detail = $"{last:MMMM yyyy}: {charges.Count} asset(s), ₦{run.Total:N2}" });
            await db.SaveChangesAsync(inner);
            return run.Id;
        }, ct);

    /// <summary>Sells or scraps an asset: its cost and depreciation leave the books; proceeds less book value is the gain (or loss).</summary>
    public Task DisposeAsync(int assetId, DisposeInput i, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            var a = await db.FixedAssets.FromSqlInterpolated($"SELECT * FROM fixed_assets WHERE Id = {assetId} FOR UPDATE").SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Asset");
            if (a.Status == AssetStatuses.Disposed) throw new BusinessRuleException("This asset has already been disposed of.");
            if (i.Proceeds < 0) throw new BusinessRuleException("Proceeds can't be negative.");
            if (i.DisposedOn < a.AcquiredOn) throw new BusinessRuleException("It can't be disposed of before it was bought.");
            if (i.ReceivedInto is not ("Bank" or "Cash")) throw new BusinessRuleException("Say where the proceeds went (Bank or Cash).");
            var gain = R(i.Proceeds - a.BookValue);
            a.Status = AssetStatuses.Disposed; a.DisposedOn = i.DisposedOn; a.DisposalProceeds = R(i.Proceeds); a.DisposalGainLoss = gain;
            gl.Branch = a.BranchId ?? user.BranchId;
            await gl.PostAsync("FixedAsset", a.Id.ToString(), "disposed", i.DisposedOn, $"Disposal of {a.Code} {a.Name}{(string.IsNullOrWhiteSpace(i.Note) ? "" : ": " + i.Note.Trim())}",
            [
                PostLine.Dr(i.ReceivedInto == "Cash" ? GlCodes.Cash : GlCodes.Bank, a.DisposalProceeds.Value),
                PostLine.Dr(GlCodes.AccumulatedDepreciation, a.AccumulatedDepreciation),
                PostLine.Cr(GlCodes.FixedAssets, a.Cost),
                gain >= 0 ? PostLine.Cr(GlCodes.OtherIncome, gain, note: "Gain on disposal") : PostLine.Dr(GlCodes.OtherExpenses, -gain, note: "Loss on disposal"),
            ], user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "ASSET_DISPOSED", Entity = "FixedAsset", EntityId = a.Id.ToString(), At = clock.UtcNow,
                Detail = $"{a.Code}: proceeds ₦{i.Proceeds:N2}, {(gain >= 0 ? "gain" : "loss")} ₦{Math.Abs(gain):N2}" });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    /// <summary>The month-by-month depreciation already charged on one asset.</summary>
    public async Task<List<(int Year, int Month, decimal Amount)>> ScheduleAsync(int assetId, CancellationToken ct = default) =>
        (await db.AssetDepreciations.AsNoTracking().Where(d => d.AssetId == assetId).OrderBy(d => d.Year).ThenBy(d => d.Month).ToListAsync(ct))
            .Select(d => (d.Year, d.Month, d.Amount)).ToList();
}
