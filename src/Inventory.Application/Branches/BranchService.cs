using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Ledger;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Branches;

public sealed record BranchDto(int Id, string Code, string Name, string? Address, string? State, string? Phone, bool IsHeadOffice, bool Active, IReadOnlyList<BranchWarehouse> Warehouses);
public sealed record BranchWarehouse(int Id, string Name);
public sealed record BranchInput(string Code, string Name, string? Address, string? State, string? Phone, bool Active = true);
public sealed record BranchPerformance(int BranchId, string Branch, decimal Sales, decimal Collected, decimal Income, decimal Expenses, decimal NetProfit, decimal StockValue);

/// <summary>
/// Branches: set them up, put each warehouse in one, and compare how each is doing. The first time branches are used a "Head office" branch
/// is created and every existing warehouse is placed in it, so nothing is left unassigned.
/// </summary>
public sealed class BranchService(IBusinessDbContext db, IClock clock, AccountingService books, LedgerSync sync)
{
    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task EnsureHeadOfficeAsync(CancellationToken ct)
    {
        if (await db.Branches.AnyAsync(ct)) return;
        var hq = new Branch { Code = "HQ", Name = "Head office", IsHeadOffice = true };
        db.Branches.Add(hq);
        await db.SaveChangesAsync(ct);
        foreach (var w in await db.Warehouses.Where(w => w.BranchId == null).ToListAsync(ct)) w.BranchId = hq.Id;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<BranchDto>> ListAsync(CancellationToken ct = default)
    {
        await EnsureHeadOfficeAsync(ct);
        var branches = await db.Branches.AsNoTracking().OrderByDescending(b => b.IsHeadOffice).ThenBy(b => b.Name).ToListAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        return branches.Select(b => new BranchDto(b.Id, b.Code, b.Name, b.Address, b.State, b.Phone, b.IsHeadOffice, b.Active,
            warehouses.Where(w => w.BranchId == b.Id).Select(w => new BranchWarehouse(w.Id, w.Name)).ToList())).ToList();
    }

    public async Task<int> SaveAsync(int? id, BranchInput i, CurrentUser user, CancellationToken ct = default)
    {
        await EnsureHeadOfficeAsync(ct);
        var code = (i.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 10 || !code.All(char.IsAsciiLetterOrDigit)) throw new BusinessRuleException("Give the branch a short code (2–10 letters or digits), e.g. IKJ.");
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Name the branch.");
        if (await db.Branches.AnyAsync(b => b.Code == code && b.Id != (id ?? 0), ct)) throw new BusinessRuleException($"Branch code {code} is already used.");
        var b = id is int existing ? await db.Branches.SingleOrDefaultAsync(x => x.Id == existing, ct) ?? throw new NotFoundException("Branch") : new Branch();
        if (b.IsHeadOffice && !i.Active) throw new BusinessRuleException("The head office can't be closed.");
        if (!i.Active && await db.Warehouses.AnyAsync(w => w.BranchId == b.Id, ct)) throw new BusinessRuleException("Move this branch's warehouses to another branch before closing it.");
        b.Code = code; b.Name = i.Name.Trim(); b.Address = N(i.Address); b.State = N(i.State); b.Phone = N(i.Phone); b.Active = i.Active;
        if (id is null) db.Branches.Add(b);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = id is null ? "BRANCH_CREATED" : "BRANCH_UPDATED", Entity = "Branch",
            EntityId = id?.ToString(), At = clock.UtcNow, Detail = $"{code} {b.Name}" });
        await db.SaveChangesAsync(ct);
        return b.Id;
    }

    public async Task AssignWarehouseAsync(int warehouseId, int branchId, CurrentUser user, CancellationToken ct = default)
    {
        await EnsureHeadOfficeAsync(ct);
        var w = await db.Warehouses.SingleOrDefaultAsync(x => x.Id == warehouseId, ct) ?? throw new NotFoundException("Warehouse");
        var b = await db.Branches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == branchId && x.Active, ct) ?? throw new BusinessRuleException("Choose an open branch.");
        w.BranchId = b.Id;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "WAREHOUSE_BRANCH_SET", Entity = "Warehouse", EntityId = warehouseId.ToString(),
            At = clock.UtcNow, Detail = $"{w.Name} → {b.Name}" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What each branch sold and collected in a period, and the stock it holds at cost.</summary>
    public async Task<List<BranchPerformance>> PerformanceAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await sync.RunAsync(ct);
        await EnsureHeadOfficeAsync(ct);
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var untilDay = to;
        var result = new List<BranchPerformance>();
        foreach (var b in await db.Branches.AsNoTracking().Where(x => x.Active).OrderByDescending(x => x.IsHeadOffice).ThenBy(x => x.Name).ToListAsync(ct))
        {
            var sales = await db.Invoices.Where(i => i.InvoiceDate >= from && i.InvoiceDate <= untilDay && i.VoidedAt == null && !i.IsSample
                    && db.Warehouses.Any(w => w.Id == i.WarehouseId && w.BranchId == b.Id))
                .SumAsync(i => (decimal?)i.TotalAmount, ct) ?? 0;
            var collected = await db.Payments.Where(p => p.PaymentDate >= start && p.PaymentDate < end
                    && db.Invoices.Any(i => i.Id == p.InvoiceId && i.VoidedAt == null && db.Warehouses.Any(w => w.Id == i.WarehouseId && w.BranchId == b.Id)))
                .SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
            var stock = await (from s in db.StockBatches join w in db.Warehouses on s.WarehouseId equals w.Id join p in db.Products on s.ProductId equals p.Id
                               where w.BranchId == b.Id select s.QuantityOnHand * p.CostPrice).SumAsync(x => (decimal?)x, ct) ?? 0;
            await books.ForBranchAsync(b.Id, ct);
            var pl = await books.ProfitAndLossAsync(from, to, ct);
            result.Add(new BranchPerformance(b.Id, b.Name, sales, collected, pl.TotalIncome, pl.TotalExpenses, pl.NetProfit, stock));
        }
        await books.ForBranchAsync(null, ct);
        return result;
    }
}
