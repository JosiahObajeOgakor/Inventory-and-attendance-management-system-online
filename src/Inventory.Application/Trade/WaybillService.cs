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
