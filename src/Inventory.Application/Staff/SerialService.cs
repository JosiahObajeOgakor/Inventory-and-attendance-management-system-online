using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Staff;

public sealed record SerialDto(int Id, string SerialNumber, string Status, int? WarehouseId, string? Warehouse, DateTime ReceivedAt, DateTime? SoldAt, string? InvoiceNumber, string? Customer, string? Notes);
public sealed record SerialDiscrepancyDto(int ProductId, string Sku, string Product, int CountedStock, int SerialsOnShelf, int Difference);

/// <summary>
/// Individually tracked units (scales, feeders) as opposed to loose feed (rule T7), ported from Serials.vb. A duplicate serial means either a
/// typo or a counterfeit, so a list containing one is refused whole.
/// </summary>
public sealed class SerialService(IBusinessDbContext db, TransactionRunner tx, IClock clock)
{
    /// <summary>Accepts a pasted block — one per line, or comma / semicolon / tab separated, as it arrives off a packing list.</summary>
    public static List<string> Split(string? pasted) =>
        string.IsNullOrWhiteSpace(pasted) ? [] : pasted.Split(['\r', '\n', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();

    private async Task<Product> TrackedProduct(int productId, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId, ct) ?? throw new NotFoundException("Product");
        if (!p.TracksSerial) throw new BusinessRuleException($"{p.Name} isn't set up to track serial numbers. Turn that on in the product first.");
        return p;
    }

    public Task ReceiveAsync(int productId, int warehouseId, IEnumerable<string> serials, string? notes, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            await TrackedProduct(productId, inner);
            if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, inner)) throw new NotFoundException("Warehouse");
            var wanted = serials.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (wanted.Count == 0) throw new BusinessRuleException("Enter at least one serial number.");
            if (wanted.Any(s => s.Length > 80)) throw new BusinessRuleException("A serial number is longer than 80 characters.");
            var dup = wanted.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0) throw new BusinessRuleException("The same serial is listed twice: " + string.Join(", ", dup));
            var clash = await db.ProductSerials.Where(s => s.ProductId == productId && wanted.Contains(s.SerialNumber)).Select(s => s.SerialNumber).ToListAsync(inner);
            if (clash.Count > 0) throw new BusinessRuleException("Already on file: " + string.Join(", ", clash));
            foreach (var sn in wanted)
                db.ProductSerials.Add(new ProductSerial { ProductId = productId, SerialNumber = sn, WarehouseId = warehouseId, Status = SerialStatuses.InStock, ReceivedAt = clock.UtcNow, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() });
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "SERIALS_RECEIVED", Entity = "Product", EntityId = productId.ToString(), At = clock.UtcNow, Detail = $"{wanted.Count} unit(s)" });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    public async Task<List<SerialDto>> ListAsync(int productId, string? status, int? warehouseId, string? search, CancellationToken ct = default)
    {
        var q = from s in db.ProductSerials.AsNoTracking().Where(x => x.ProductId == productId)
                join w in db.Warehouses.AsNoTracking() on s.WarehouseId equals w.Id into ws from w in ws.DefaultIfEmpty()
                join i in db.Invoices.AsNoTracking() on s.InvoiceId equals i.Id into iss from i in iss.DefaultIfEmpty()
                join c in db.Customers.AsNoTracking() on (i != null ? i.CustomerId : 0) equals c.Id into cs from c in cs.DefaultIfEmpty()
                select new { s, Warehouse = w != null ? w.Name : null, Invoice = i != null ? i.InvoiceNumber : null, Customer = c != null ? c.Name : null };
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.s.Status == status);
        if (warehouseId is int wid) q = q.Where(r => r.s.WarehouseId == wid);
        if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim(); q = q.Where(r => r.s.SerialNumber.Contains(t)); }
        var rows = await q.OrderBy(r => r.s.ReceivedAt).ThenBy(r => r.s.Id).Take(500).ToListAsync(ct);
        return rows.Select(r => new SerialDto(r.s.Id, r.s.SerialNumber, r.s.Status, r.s.WarehouseId, r.Warehouse, r.s.ReceivedAt, r.s.SoldAt, r.Invoice, r.Customer, r.s.Notes)).ToList();
    }

    /// <summary>A customer brings a unit back: it returns to the shelf and is sellable again (only a Sold unit can be taken back).</summary>
    public Task TakeBackAsync(int productId, string serial, int warehouseId, string? reason, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            var s = await db.ProductSerials.FromSqlInterpolated($"SELECT * FROM product_serials WHERE ProductId = {productId} AND SerialNumber = {serial} FOR UPDATE").SingleOrDefaultAsync(inner);
            if (s is null || s.Status != SerialStatuses.Sold) throw new BusinessRuleException("That serial isn't recorded as sold, so there is nothing to take back.");
            if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, inner)) throw new NotFoundException("Warehouse");
            s.Status = SerialStatuses.Returned; s.WarehouseId = warehouseId; s.Notes = string.IsNullOrWhiteSpace(reason) ? s.Notes : reason.Trim();
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "SERIAL_TAKEN_BACK", Entity = "ProductSerial", EntityId = s.Id.ToString(), At = clock.UtcNow, Detail = serial });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    public Task WriteOffAsync(int productId, string serial, string reason, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("Say why the unit is being written off.");
            var s = await db.ProductSerials.FromSqlInterpolated($"SELECT * FROM product_serials WHERE ProductId = {productId} AND SerialNumber = {serial} FOR UPDATE").SingleOrDefaultAsync(inner);
            if (s is null || s.Status is not (SerialStatuses.InStock or SerialStatuses.Returned)) throw new BusinessRuleException("Only a unit still on the shelf can be written off.");
            s.Status = SerialStatuses.WrittenOff; s.Notes = reason.Trim();
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "SERIAL_WRITTEN_OFF", Entity = "ProductSerial", EntityId = s.Id.ToString(), At = clock.UtcNow, Detail = $"{serial}: {reason.Trim()}" });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    /// <summary>Where counted stock and the serial register disagree — what a stock-take is looking for.</summary>
    public async Task<List<SerialDiscrepancyDto>> DiscrepanciesAsync(CancellationToken ct = default)
    {
        var counted = await db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var reg = await db.ProductSerials.AsNoTracking().Where(s => s.Status == SerialStatuses.InStock).GroupBy(s => s.ProductId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var tracked = await db.Products.AsNoTracking().Where(p => p.TracksSerial).ToListAsync(ct);
        return tracked.Select(p => new SerialDiscrepancyDto(p.Id, p.Sku, p.Name, counted.GetValueOrDefault(p.Id), reg.GetValueOrDefault(p.Id), counted.GetValueOrDefault(p.Id) - reg.GetValueOrDefault(p.Id)))
            .Where(d => d.Difference != 0).OrderByDescending(d => Math.Abs(d.Difference)).ToList();
    }
}
