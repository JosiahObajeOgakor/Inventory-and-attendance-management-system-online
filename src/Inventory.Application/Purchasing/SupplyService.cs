using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Purchasing;

public sealed class SupplyLineDto
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class SupplyRequest
{
    public int SupplierId { get; set; }
    public DateOnly? SupplyDate { get; set; }
    public decimal PaidNow { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public string? Note { get; set; }
    public List<SupplyLineDto> Lines { get; set; } = [];
}

public sealed record SupplyResult(int Id, string Reference, decimal Total, decimal Paid, decimal Outstanding, string PaymentStatus);
/// <summary>What a delete removed, so the person is told plainly rather than just "done".</summary>
public sealed record SupplyDeleteResult(int Records, decimal Value);

public sealed class SupplyRequestValidator : AbstractValidator<SupplyRequest>
{
    public SupplyRequestValidator()
    {
        RuleFor(x => x.SupplierId).GreaterThan(0);
        RuleFor(x => x.PaidNow).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Note).MaximumLength(400);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item the supplier supplied.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).GreaterThan(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitCost).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>
/// Records what a supplier supplied, kept entirely separate from stock and from purchase orders: no stock batch, no stock movement, no change
/// to a product's cost price and no ledger entry. A supply's own total, what was paid on it and what is still owed are its whole truth, which
/// is why an admin can delete supplies — singly, per supplier, per month or all of them — without unwinding anything else in the business.
/// </summary>
public sealed class SupplyService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, IValidator<SupplyRequest> validator)
{
    public async Task<SupplyResult> CreateAsync(SupplyRequest req, CurrentUser user, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        return await tx.RunAsync(async inner =>
        {
            if (!await db.Suppliers.AnyAsync(s => s.Id == req.SupplierId, inner)) throw new NotFoundException("Supplier");
            var ids = req.Lines.Select(l => l.ProductId).Distinct().ToList();
            if (await db.Products.CountAsync(p => ids.Contains(p.Id), inner) != ids.Count) throw new NotFoundException("Product");

            var total = req.Lines.Sum(l => Money.Round(l.Quantity * l.UnitCost));
            var paid = Math.Min(Math.Max(0m, req.PaidNow), total);   // never record paying more than the supply is worth
            var supply = new Supply
            {
                Reference = await DocumentNumbers.NextAsync(company, clock, (n, c) => db.Supplies.AnyAsync(s => s.Reference == n, c), inner),
                SupplierId = req.SupplierId,
                SupplyDate = req.SupplyDate ?? clock.BusinessToday,
                TotalAmount = total,
                AmountPaid = paid,
                PaymentStatus = PaymentStatuses.For(paid, total),
                PaymentMethod = string.IsNullOrWhiteSpace(req.PaymentMethod) ? "Cash" : req.PaymentMethod.Trim(),
                Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
                CreatedByUserId = user.Id,
                CreatedAt = clock.UtcNow,
                Items = req.Lines.Select(l => new SupplyItem
                {
                    ProductId = l.ProductId, Quantity = l.Quantity, UnitCost = l.UnitCost, LineTotal = Money.Round(l.Quantity * l.UnitCost),
                }).ToList(),
            };
            db.Supplies.Add(supply);
            await Audit(user, "SUPPLY_RECORDED", supply.Id.ToString(), $"{supply.Reference} ₦{total:N2} ({supply.Items.Count} item(s))", inner);
            return new SupplyResult(supply.Id, supply.Reference, total, paid, total - paid, supply.PaymentStatus);
        }, ct);
    }

    /// <summary>Money paid against one supply, capped at what that supply still owes.</summary>
    public Task<SupplyResult> PayAsync(int id, decimal amount, string method, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            if (amount <= 0) throw new BusinessRuleException("Enter an amount greater than zero.");
            var s = await db.Supplies.FromSqlInterpolated($"SELECT * FROM supplies WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(inner)
                ?? throw new NotFoundException("Supply");
            var owed = s.TotalAmount - s.AmountPaid;
            if (owed <= 0) throw new BusinessRuleException("This supply is already fully paid.");
            if (amount > owed) throw new BusinessRuleException($"That is more than this supply still owes (₦{owed:N2}).");

            s.AmountPaid += amount;
            s.PaymentStatus = PaymentStatuses.For(s.AmountPaid, s.TotalAmount);
            if (!string.IsNullOrWhiteSpace(method)) s.PaymentMethod = method.Trim()[..Math.Min(30, method.Trim().Length)];
            await Audit(user, "SUPPLY_PAID", s.Id.ToString(), $"{s.Reference} ₦{amount:N2} via {s.PaymentMethod}", inner);
            return new SupplyResult(s.Id, s.Reference, s.TotalAmount, s.AmountPaid, s.TotalAmount - s.AmountPaid, s.PaymentStatus);
        }, ct);

    public Task<SupplyDeleteResult> DeleteAsync(int id, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var s = await db.Supplies.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, inner) ?? throw new NotFoundException("Supply");
            var name = await db.Suppliers.Where(x => x.Id == s.SupplierId).Select(x => x.Name).SingleOrDefaultAsync(inner) ?? "";
            db.Supplies.Remove(s);   // the items go with it (cascade); nothing else in the business refers to a supply
            await Audit(user, "SUPPLY_DELETED", s.Id.ToString(), $"{s.Reference} ₦{s.TotalAmount:N2} from {name}", inner);
            return new SupplyDeleteResult(1, s.TotalAmount);
        }, ct);

    public Task<SupplyDeleteResult> DeleteForSupplierAsync(int supplierId, CurrentUser user, CancellationToken ct = default) =>
        DeleteWhereAsync(s => s.SupplierId == supplierId, user,
            async (inner, r) => $"every supply from {await db.Suppliers.Where(x => x.Id == supplierId).Select(x => x.Name).SingleOrDefaultAsync(inner) ?? supplierId.ToString()}: {r.Records} record(s), ₦{r.Value:N2}", ct);

    public Task<SupplyDeleteResult> DeleteForMonthAsync(int year, int month, CurrentUser user, CancellationToken ct = default)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100) throw new BusinessRuleException("Choose a valid month.");
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        return DeleteWhereAsync(s => s.SupplyDate >= from && s.SupplyDate < to, user,
            (_, r) => Task.FromResult($"every supply in {from:MMMM yyyy}: {r.Records} record(s), ₦{r.Value:N2}"), ct);
    }

    public Task<SupplyDeleteResult> DeleteAllAsync(CurrentUser user, CancellationToken ct = default) =>
        DeleteWhereAsync(_ => true, user, (_, r) => Task.FromResult($"ALL supply records: {r.Records} record(s), ₦{r.Value:N2}"), ct);

    private Task<SupplyDeleteResult> DeleteWhereAsync(System.Linq.Expressions.Expression<Func<Supply, bool>> where, CurrentUser user,
        Func<CancellationToken, SupplyDeleteResult, Task<string>> detail, CancellationToken ct) =>
        tx.RunAsync(async inner =>
        {
            var rows = await db.Supplies.Where(where).Select(s => new { s.Id, s.TotalAmount }).ToListAsync(inner);
            var result = new SupplyDeleteResult(rows.Count, rows.Sum(r => r.TotalAmount));
            if (rows.Count > 0)
            {
                var ids = rows.Select(r => r.Id).ToList();
                await db.SupplyItems.Where(i => ids.Contains(i.SupplyId)).ExecuteDeleteAsync(inner);
                await db.Supplies.Where(where).ExecuteDeleteAsync(inner);
            }
            await Audit(user, "SUPPLIES_CLEARED", null, await detail(inner, result), inner);
            return result;
        }, ct);

    private async Task Audit(CurrentUser user, string action, string? entityId, string detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = user.Id, UserName = user.FullName, Action = action, Entity = "Supply", EntityId = entityId, At = clock.UtcNow, Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
