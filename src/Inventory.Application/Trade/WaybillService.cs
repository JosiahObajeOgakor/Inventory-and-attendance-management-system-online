using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Queries;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Trade;

public sealed class WaybillInput
{
    public int InvoiceId { get; set; }
    public DateOnly? IssueDate { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPhone { get; set; }
    public string? VehiclePlate { get; set; }
    public string? DestinationAddress { get; set; }
    public string? Notes { get; set; }
}

public sealed record WaybillRowDto(int Id, string WaybillNumber, DateOnly IssueDate, int InvoiceId, string InvoiceNumber, string Customer, string? DriverName, string? VehiclePlate);
public sealed record WaybillDetailDto(int Id, string WaybillNumber, DateOnly IssueDate, int InvoiceId, string InvoiceNumber, string? DriverName, string? DriverPhone,
    string? VehiclePlate, string? DestinationAddress, string? Notes);
public sealed record PendingInvoiceDto(int InvoiceId, string InvoiceNumber, DateOnly InvoiceDate, string Customer, string? Warehouse, decimal TotalAmount, int WaybillCount);
public sealed record WaybillPrefillDto(string InvoiceNumber, string Customer, string Phone, string Address, string Warehouse);

public sealed class WaybillInputValidator : AbstractValidator<WaybillInput>
{
    public WaybillInputValidator()
    {
        RuleFor(x => x.InvoiceId).GreaterThan(0);
        RuleFor(x => x.DriverName).MaximumLength(120);
        RuleFor(x => x.DriverPhone).MaximumLength(30);
        RuleFor(x => x.VehiclePlate).MaximumLength(20);
        RuleFor(x => x.DestinationAddress).MaximumLength(250);
        RuleFor(x => x.Notes).MaximumLength(250);
    }
}

/// <summary>A delivery note per invoice (driver, vehicle, destination), printed with the company's dispatch stamp.</summary>
public sealed class WaybillService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company)
{
    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<int> CreateAsync(WaybillInput input, CurrentUser user, CancellationToken ct = default)
    {
        await new WaybillInputValidator().ValidateAndThrowAsync(input, ct);
        return await tx.RunAsync(async inner =>
        {
            var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == input.InvoiceId, inner) ?? throw new NotFoundException("Invoice");
            if (invoice.Status == PaymentStatuses.Voided) throw new BusinessRuleException("A voided sale can't be dispatched.");
            var number = await DocumentNumbers.NextAsync(company, clock, (n, c) => db.Waybills.AnyAsync(w => w.WaybillNumber == n, c), inner);
            var w = new Waybill
            {
                WaybillNumber = number, InvoiceId = invoice.Id, IssueDate = input.IssueDate ?? clock.BusinessToday, DriverName = N(input.DriverName), DriverPhone = N(input.DriverPhone),
                VehiclePlate = N(input.VehiclePlate), DestinationAddress = N(input.DestinationAddress), Notes = N(input.Notes), CreatedByUserId = user.Id,
            };
            db.Waybills.Add(w);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "WAYBILL_CREATED", Entity = "Waybill", At = clock.UtcNow, Detail = $"{number} for {invoice.InvoiceNumber}" });
            await db.SaveChangesAsync(inner);
            return w.Id;
        }, ct);
    }

    public async Task<WaybillDetailDto> GetAsync(int id, CancellationToken ct)
    {
        var w = await db.Waybills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Waybill");
        var inv = await db.Invoices.AsNoTracking().Where(i => i.Id == w.InvoiceId).Select(i => i.InvoiceNumber).SingleAsync(ct);
        return new WaybillDetailDto(w.Id, w.WaybillNumber, w.IssueDate, w.InvoiceId, inv, w.DriverName, w.DriverPhone, w.VehiclePlate, w.DestinationAddress, w.Notes);
    }

    /// <summary>Corrects a waybill's dispatch details. The sale it belongs to never changes (issue a new waybill for another sale).</summary>
    public async Task UpdateAsync(int id, WaybillInput input, CurrentUser user, CancellationToken ct = default)
    {
        var w0 = await db.Waybills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Waybill");
        input.InvoiceId = w0.InvoiceId;
        await new WaybillInputValidator().ValidateAndThrowAsync(input, ct);
        await tx.RunAsync<bool>(async inner =>
        {
            var w = await db.Waybills.FromSqlInterpolated($"SELECT * FROM waybills WHERE Id = {id} FOR UPDATE").SingleAsync(inner);
            var changes = new List<string>();
            void Set(string label, string? before, string? after, Action<string?> apply)
            {
                if (before == after) return;
                apply(after);
                changes.Add($"{label}: {before ?? "—"} → {after ?? "—"}");
            }
            Set("driver", w.DriverName, N(input.DriverName), v => w.DriverName = v);
            Set("phone", w.DriverPhone, N(input.DriverPhone), v => w.DriverPhone = v);
            Set("vehicle", w.VehiclePlate, N(input.VehiclePlate), v => w.VehiclePlate = v);
            Set("destination", w.DestinationAddress, N(input.DestinationAddress), v => w.DestinationAddress = v);
            Set("notes", w.Notes, N(input.Notes), v => w.Notes = v);
            var date = input.IssueDate ?? w.IssueDate;
            if (date != w.IssueDate) { changes.Add($"date: {w.IssueDate:yyyy-MM-dd} → {date:yyyy-MM-dd}"); w.IssueDate = date; }
            if (changes.Count == 0) return true;

            var detail = $"{w.WaybillNumber}: {string.Join("; ", changes)}";
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "WAYBILL_EDITED", Entity = "Waybill", EntityId = w.Id.ToString(), At = clock.UtcNow,
                Detail = detail.Length > 1000 ? detail[..1000] : detail });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);
    }

    public async Task<PagedResult<WaybillRowDto>> ListAsync(PageRequest page, CancellationToken ct)
    {
        var q = from w in db.Waybills.AsNoTracking() join i in db.Invoices.AsNoTracking() on w.InvoiceId equals i.Id join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id
                select new { w, i.InvoiceNumber, Customer = c.Name };
        if (page.Term is { } t) q = q.Where(r => r.w.WaybillNumber.Contains(t) || r.InvoiceNumber.Contains(t) || r.Customer.Contains(t) || (r.w.DriverName != null && r.w.DriverName.Contains(t)));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.w.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new PagedResult<WaybillRowDto>(rows.Select(r => new WaybillRowDto(r.w.Id, r.w.WaybillNumber, r.w.IssueDate, r.w.InvoiceId, r.InvoiceNumber, r.Customer, r.w.DriverName, r.w.VehiclePlate)).ToList(), total, page.SafePage, page.SafeSize);
    }

    /// <summary>Sales to choose a waybill for. <paramref name="pendingOnly"/> hides sales that already have one (the desktop "not yet dispatched" filter).</summary>
    public async Task<PagedResult<PendingInvoiceDto>> InvoicesAsync(PageRequest page, bool pendingOnly, CancellationToken ct)
    {
        var q = from i in db.Invoices.AsNoTracking()
                where i.Status != PaymentStatuses.Voided
                join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id
                select new
                {
                    i, Customer = c.Name,
                    Warehouse = db.Warehouses.Where(w => w.Id == i.WarehouseId).Select(w => w.Name).FirstOrDefault(),
                    Count = db.Waybills.Count(w => w.InvoiceId == i.Id),
                };
        if (pendingOnly) q = q.Where(r => r.Count == 0);
        if (page.Term is { } t) q = q.Where(r => r.i.InvoiceNumber.Contains(t) || r.Customer.Contains(t));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.i.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new PagedResult<PendingInvoiceDto>(rows.Select(r => new PendingInvoiceDto(r.i.Id, r.i.InvoiceNumber, r.i.InvoiceDate, r.Customer, r.Warehouse, r.i.TotalAmount, r.Count)).ToList(), total, page.SafePage, page.SafeSize);
    }

    public async Task<WaybillPrefillDto> PrefillAsync(int invoiceId, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == i.CustomerId, ct);
        var wh = i.WarehouseId is int w ? await db.Warehouses.AsNoTracking().Where(x => x.Id == w).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
        return new WaybillPrefillDto(i.InvoiceNumber, c.Name, c.Phone ?? "", string.Join(", ", new[] { c.Address, c.Location }.Where(s => !string.IsNullOrWhiteSpace(s))), wh ?? "");
    }
}
