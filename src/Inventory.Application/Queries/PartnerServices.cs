using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Queries;

public sealed class SupplierInputValidator : AbstractValidator<SupplierInput>
{
    public SupplierInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Email).MaximumLength(100).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Address).MaximumLength(200);
        RuleFor(x => x.TaxId).MaximumLength(40);
    }
}

public sealed class CustomerInputValidator : AbstractValidator<CustomerInput>
{
    public CustomerInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.CustomerType).Must(t => CustomerTypes.All.Contains(t)).WithMessage("Unknown customer type.");
        RuleFor(x => x.Email).MaximumLength(100).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.RebatePerUnit).InclusiveBetween(0, 1_000_000).WithMessage("Enter a rebate amount between ₦0 and ₦1,000,000 per unit.");
        RuleFor(x => x.CreditLimit).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Phone).MaximumLength(30);
    }
}

public sealed class PartnerService(IBusinessDbContext db, IClock clock)
{
    private AuditLog Audit(CurrentUser u, string action, string entity, int id, string detail) => new()
    {
        UserId = u.Id, UserName = u.FullName, Action = action, Entity = entity, EntityId = id.ToString(), At = clock.UtcNow, Detail = detail,
    };

    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---- suppliers ----
    public async Task<int> CreateSupplierAsync(SupplierInput i, CurrentUser user, CancellationToken ct)
    {
        await new SupplierInputValidator().ValidateAndThrowAsync(i, ct);
        var s = new Supplier { Name = i.Name.Trim(), Category = N(i.Category), ContactName = N(i.ContactName), Phone = N(i.Phone), Email = N(i.Email), Address = N(i.Address), TaxId = N(i.TaxId) };
        db.Suppliers.Add(s);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(Audit(user, "SUPPLIER_CREATED", "Supplier", s.Id, s.Name));
        await db.SaveChangesAsync(ct);
        return s.Id;
    }

    public async Task UpdateSupplierAsync(int id, SupplierInput i, CurrentUser user, CancellationToken ct)
    {
        await new SupplierInputValidator().ValidateAndThrowAsync(i, ct);
        var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier");
        s.Name = i.Name.Trim(); s.Category = N(i.Category); s.ContactName = N(i.ContactName); s.Phone = N(i.Phone);
        s.Email = N(i.Email); s.Address = N(i.Address); s.TaxId = N(i.TaxId);
        db.AuditLogs.Add(Audit(user, "SUPPLIER_UPDATED", "Supplier", id, s.Name));
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSupplierAsync(int id, CurrentUser user, CancellationToken ct)
    {
        var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier");
        if (await db.PurchaseOrders.AnyAsync(p => p.SupplierId == id, ct))
            throw new BusinessRuleException($"\"{s.Name}\" has purchase orders and can't be deleted.");
        db.Suppliers.Remove(s);
        db.AuditLogs.Add(Audit(user, "SUPPLIER_DELETED", "Supplier", id, s.Name));
        await db.SaveChangesAsync(ct);
    }

    // ---- the goods each supplier sells us ----
    public async Task<List<SupplierItemDto>> SupplierItemsAsync(int supplierId, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId, ct)) throw new NotFoundException("Supplier");
        return await (from si in db.SupplierItems.AsNoTracking().Where(x => x.SupplierId == supplierId)
                      join p in db.Products.AsNoTracking() on si.ProductId equals p.Id
                      orderby p.Name
                      select new SupplierItemDto(si.Id, p.Id, p.Name, p.Sku, p.Unit, si.UnitCost > 0 ? si.UnitCost : p.CostPrice, p.IsActive)).ToListAsync(ct);
    }

    /// <summary>Replaces the supplier's list with these products (and usual costs). Products not listed are dropped from the list; nothing else changes.</summary>
    public async Task SetSupplierItemsAsync(int supplierId, IReadOnlyList<SupplierItemInput> items, CurrentUser user, CancellationToken ct)
    {
        var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == supplierId, ct) ?? throw new NotFoundException("Supplier");
        if (items.Any(i => i.UnitCost < 0)) throw new BusinessRuleException("A cost can't be negative.");
        var wanted = items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Last().UnitCost);
        var known = await db.Products.Where(p => wanted.Keys.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        if (known.Count != wanted.Count) throw new NotFoundException("Product");
        var existing = await db.SupplierItems.Where(x => x.SupplierId == supplierId).ToListAsync(ct);
        db.SupplierItems.RemoveRange(existing.Where(e => !wanted.ContainsKey(e.ProductId)));
        foreach (var (productId, cost) in wanted)
        {
            var row = existing.FirstOrDefault(e => e.ProductId == productId);
            if (row is null) db.SupplierItems.Add(new SupplierItem { SupplierId = supplierId, ProductId = productId, UnitCost = cost });
            else row.UnitCost = cost;
        }
        db.AuditLogs.Add(Audit(user, "SUPPLIER_ITEMS_SET", "Supplier", supplierId, $"{s.Name}: {wanted.Count} item(s)"));
        await db.SaveChangesAsync(ct);
    }

    // ---- customers ----
    public async Task<int> CreateCustomerAsync(CustomerInput i, CurrentUser user, CancellationToken ct)
    {
        await new CustomerInputValidator().ValidateAndThrowAsync(i, ct);
        var c = new Customer();
        Apply(c, i);
        db.Customers.Add(c);   // Balance starts at 0: a new customer never carries a debt they weren't sold
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(Audit(user, "CUSTOMER_CREATED", "Customer", c.Id, c.Name));
        await db.SaveChangesAsync(ct);
        return c.Id;
    }

    public async Task UpdateCustomerAsync(int id, CustomerInput i, CurrentUser user, CancellationToken ct)
    {
        await new CustomerInputValidator().ValidateAndThrowAsync(i, ct);
        var c = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer");
        Apply(c, i);
        db.AuditLogs.Add(Audit(user, "CUSTOMER_UPDATED", "Customer", id, c.Name));
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteCustomerAsync(int id, CurrentUser user, CancellationToken ct)
    {
        var c = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer");
        if (await db.Invoices.AnyAsync(i => i.CustomerId == id, ct) || await db.RebateEntries.AnyAsync(r => r.CustomerId == id, ct))
            throw new BusinessRuleException($"\"{c.Name}\" has sales history and can't be deleted.");
        db.Customers.Remove(c);
        db.AuditLogs.Add(Audit(user, "CUSTOMER_DELETED", "Customer", id, c.Name));
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(Customer c, CustomerInput i)
    {
        c.Name = i.Name.Trim(); c.CustomerType = i.CustomerType; c.ContactName = N(i.ContactName); c.Phone = N(i.Phone);
        c.Location = N(i.Location); c.Address = N(i.Address); c.Email = N(i.Email); c.TaxId = N(i.TaxId);
        c.RebatePerUnit = i.RebatePerUnit; c.CreditLimit = i.CreditLimit;
    }

    // ---- reference data ----
    public async Task<int> CreateCategoryAsync(string name, CurrentUser user, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 60) throw new BusinessRuleException("Enter a category name (up to 60 characters).");
        if (await db.Categories.AnyAsync(c => c.Name == name, ct)) throw new BusinessRuleException("That category already exists.");
        var c = new Category { Name = name };
        db.Categories.Add(c);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(Audit(user, "CATEGORY_CREATED", "Category", c.Id, name));
        await db.SaveChangesAsync(ct);
        return c.Id;
    }

    public async Task<int> CreateWarehouseAsync(string name, string? location, CurrentUser user, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 80) throw new BusinessRuleException("Enter a warehouse name (up to 80 characters).");
        if (await db.Warehouses.AnyAsync(w => w.Name == name, ct)) throw new BusinessRuleException("That warehouse already exists.");
        var w = new Warehouse { Name = name, Location = N(location) };
        db.Warehouses.Add(w);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(Audit(user, "WAREHOUSE_CREATED", "Warehouse", w.Id, name));
        await db.SaveChangesAsync(ct);
        return w.Id;
    }
}
