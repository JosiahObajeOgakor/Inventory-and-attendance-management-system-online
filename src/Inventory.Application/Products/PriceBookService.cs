using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Queries;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Products;

public sealed record PriceBookRowDto(int ProductId, string Category, string Product, string Sku, string Unit, decimal Distributor, decimal Wholesaler, decimal Retail, int InStock, DateTime? LastChanged);
public sealed record PriceBookDto(PagedResult<PriceBookRowDto> Page, DateTime? LastUpdated, int ChangesThisMonth);
public sealed record PriceHistoryDto(long Id, DateTime ChangedAt, string Product, decimal DistributorWas, decimal DistributorNow, decimal WholesalerWas, decimal WholesalerNow,
    decimal RetailWas, decimal RetailNow, string ChangedBy, string Note);
public sealed record PriceUpdateInput(int ProductId, decimal Distributor, decimal Wholesaler, decimal Retail);
public sealed record NewPriceProduct(string Name, int CategoryId, string? Unit, decimal Distributor, decimal Wholesaler, decimal Retail);

/// <summary>
/// The price book (Candid Purrfect): distributor / wholesaler / retail price per product, every change kept with what it was, what it
/// became, who changed it and why. Ported from PriceBook.vb; only companies with <c>HasPriceLists</c> get it.
/// </summary>
public sealed class PriceBookService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, IUserDirectory users)
{
    private void Require() { if (!company.HasPriceLists) throw new NotFoundException("Price book"); }

    /// <summary>A price moved by <paramref name="percent"/>, rounded to the nearest <paramref name="roundTo"/> naira (0 = to the kobo).</summary>
    public static decimal Adjusted(decimal price, decimal percent, decimal roundTo)
    {
        var v = price * (1m + percent / 100m);
        if (roundTo > 0) v = Math.Round(v / roundTo, MidpointRounding.AwayFromZero) * roundTo;
        return Math.Max(0m, Math.Round(v, 2, MidpointRounding.AwayFromZero));
    }

    public async Task<PriceBookDto> CurrentAsync(PageRequest page, int? categoryId, CancellationToken ct)
    {
        Require();
        var q = from p in db.Products.AsNoTracking().Where(x => x.IsActive) join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id select new { p, Category = c.Name };
        if (categoryId is int cid) q = q.Where(r => r.p.CategoryId == cid);
        if (page.Term is { } t) q = q.Where(r => r.p.Name.Contains(t) || r.p.Sku.Contains(t) || r.Category.Contains(t));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.Category).ThenBy(r => r.p.Name).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        var ids = rows.Select(r => r.p.Id).ToList();
        var stockBy = await db.StockBatches.AsNoTracking().Where(b => ids.Contains(b.ProductId)).GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var last = await db.PriceChanges.AsNoTracking().Where(c => ids.Contains(c.ProductId)).GroupBy(c => c.ProductId).Select(g => new { g.Key, At = g.Max(c => c.ChangedAt) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var lastAll = await db.PriceChanges.AsNoTracking().MaxAsync(c => (DateTime?)c.ChangedAt, ct);
        var monthStart = clock.BusinessToday.AddDays(1 - clock.BusinessToday.Day).ToDateTime(TimeOnly.MinValue).AddHours(-1);
        var thisMonth = await db.PriceChanges.AsNoTracking().CountAsync(c => c.ChangedAt >= monthStart, ct);
        return new PriceBookDto(new PagedResult<PriceBookRowDto>(rows.Select(r => new PriceBookRowDto(r.p.Id, r.Category, r.p.Name, r.p.Sku, r.p.Unit, r.p.PriceDistributor, r.p.PriceWholesaler, r.p.PriceRetail,
            stockBy.GetValueOrDefault(r.p.Id), last.TryGetValue(r.p.Id, out var at) ? at : null)).ToList(), total, page.SafePage, page.SafeSize), lastAll, thisMonth);
    }

    public async Task<PagedResult<PriceHistoryDto>> HistoryAsync(PageRequest page, int? productId, CancellationToken ct)
    {
        Require();
        var q = from c in db.PriceChanges.AsNoTracking() join p in db.Products.AsNoTracking() on c.ProductId equals p.Id select new { c, p.Name };
        if (productId is int pid) q = q.Where(r => r.c.ProductId == pid);
        if (page.Term is { } t) q = q.Where(r => r.Name.Contains(t) || (r.c.Note != null && r.c.Note.Contains(t)));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.c.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        var briefs = await users.BriefsAsync(rows.Where(r => r.c.ChangedByUserId.HasValue).Select(r => r.c.ChangedByUserId!.Value), ct);
        return new PagedResult<PriceHistoryDto>(rows.Select(r => new PriceHistoryDto(r.c.Id, r.c.ChangedAt, r.Name, r.c.OldDistributor, r.c.NewDistributor, r.c.OldWholesaler, r.c.NewWholesaler, r.c.OldRetail, r.c.NewRetail,
            r.c.ChangedByUserId is int u && briefs.TryGetValue(u, out var b) ? b.FullName : "", r.c.Note ?? "")).ToList(), total, page.SafePage, page.SafeSize);
    }

    /// <summary>Saves new prices in one go and records each real change. Unchanged lines are skipped so re-saving writes no noise. All or nothing; returns how many products changed.</summary>
    public Task<int> ApplyAsync(IReadOnlyList<PriceUpdateInput> updates, string? note, CurrentUser user, CancellationToken ct)
    {
        Require();
        if (updates.Any(u => u.Distributor < 0 || u.Wholesaler < 0 || u.Retail < 0)) throw new BusinessRuleException("A price can't be negative.");
        if (updates.Any(u => u.Distributor > 999_999_999 || u.Wholesaler > 999_999_999 || u.Retail > 999_999_999)) throw new BusinessRuleException("A price is too large.");
        return tx.RunAsync(async inner =>
        {
            var changed = 0;
            foreach (var u in updates.DistinctBy(x => x.ProductId).OrderBy(x => x.ProductId))
            {
                var p = await db.Products.FromSqlInterpolated($"SELECT * FROM products WHERE Id = {u.ProductId} FOR UPDATE").SingleOrDefaultAsync(inner);
                if (p is null || (p.PriceDistributor == u.Distributor && p.PriceWholesaler == u.Wholesaler && p.PriceRetail == u.Retail)) continue;
                db.PriceChanges.Add(new PriceChange { ProductId = p.Id, OldDistributor = p.PriceDistributor, OldWholesaler = p.PriceWholesaler, OldRetail = p.PriceRetail,
                    NewDistributor = u.Distributor, NewWholesaler = u.Wholesaler, NewRetail = u.Retail, ChangedAt = clock.UtcNow, ChangedByUserId = user.Id, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
                p.PriceDistributor = u.Distributor; p.PriceWholesaler = u.Wholesaler; p.PriceRetail = u.Retail; p.SellingPrice = u.Retail;
                changed++;
            }
            if (changed > 0) db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PRICES_UPDATED", Entity = "Product", At = clock.UtcNow, Detail = $"{changed} product(s)" });
            await db.SaveChangesAsync(inner);
            return changed;
        }, ct);
    }

    /// <summary>Adds products straight onto the price list: own code (<c>CP-00012</c>), own in-store barcode, a first history line and no stock. All or nothing.</summary>
    public Task<List<int>> AddProductsAsync(IReadOnlyList<NewPriceProduct> products, string? note, CurrentUser user, CancellationToken ct)
    {
        Require();
        var lines = products.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        if (lines.Count == 0) throw new BusinessRuleException("Type at least one product.");
        if (lines.Any(p => p.Distributor < 0 || p.Wholesaler < 0 || p.Retail < 0)) throw new BusinessRuleException("A price can't be negative.");
        if (lines.Any(p => p.Name.Trim().Length > 150)) throw new BusinessRuleException("A product name is longer than 150 characters.");
        var dup = lines.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new BusinessRuleException($"\"{dup.Key}\" is on the list twice.");
        var prefix = new string(company.DocumentPrefix.Where(char.IsUpper).ToArray());
        if (prefix.Length == 0) prefix = "P";
        return tx.RunAsync(async inner =>
        {
            var ids = new List<int>();
            foreach (var l in lines)
            {
                var name = l.Name.Trim();
                if (await db.Products.AnyAsync(x => x.Name == name && x.IsActive, inner)) throw new BusinessRuleException($"\"{name}\" is already on the price list.");
                if (!await db.Categories.AnyAsync(c => c.Id == l.CategoryId, inner)) throw new NotFoundException("Category");
                var p = new Product
                {
                    Sku = "NEW-" + Guid.NewGuid().ToString("N")[..12], Name = name, CategoryId = l.CategoryId, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "Bag" : l.Unit.Trim(),
                    PriceDistributor = l.Distributor, PriceWholesaler = l.Wholesaler, PriceRetail = l.Retail, SellingPrice = l.Retail,
                };
                db.Products.Add(p);
                await db.SaveChangesAsync(inner);
                p.Sku = $"{prefix}-{p.Id:D5}"; p.Barcode = Barcodes.MintInternalBarcode(p.Id);
                db.PriceChanges.Add(new PriceChange { ProductId = p.Id, NewDistributor = l.Distributor, NewWholesaler = l.Wholesaler, NewRetail = l.Retail, ChangedAt = clock.UtcNow, ChangedByUserId = user.Id,
                    Note = string.IsNullOrWhiteSpace(note) ? "Added to price list" : note.Trim() });
                ids.Add(p.Id);
            }
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PRICE_LIST_PRODUCTS_ADDED", Entity = "Product", At = clock.UtcNow, Detail = $"{ids.Count} product(s)" });
            await db.SaveChangesAsync(inner);
            return ids;
        }, ct);
    }
}
