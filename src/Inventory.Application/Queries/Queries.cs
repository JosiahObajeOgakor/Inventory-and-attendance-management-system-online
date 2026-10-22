using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Queries;

/// <summary>Resolves user ids (stored as plain ints in company data) to names from the shared identity store.</summary>
public sealed record UserBrief(string FullName, string Role);

public interface IUserDirectory
{
    Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids, CancellationToken ct = default);
    Task<Dictionary<int, UserBrief>> BriefsAsync(IEnumerable<int> ids, CancellationToken ct = default);
}

public sealed class CatalogQueries(IBusinessDbContext db, IUserDirectory users, IClock clock)
{
    public async Task<PagedResult<ProductDto>> ProductsAsync(PageRequest page, bool includeInactive, bool isAdmin, CancellationToken ct)
    {
        var q = db.Products.AsNoTracking().Where(p => includeInactive || p.IsActive);
        foreach (var w in SearchTerms.Words(page.Term))
            q = q.Where(p => p.Name.Contains(w) || p.Sku.Contains(w) || (p.Barcode != null && p.Barcode.Contains(w)) || p.Unit.Contains(w) || p.Category!.Name.Contains(w)
                || (p.Species != null && p.Species.Contains(w)) || (p.LifeStage != null && p.LifeStage.Contains(w)));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize)
            .Select(p => new { p, Category = p.Category!.Name }).ToListAsync(ct);
        var ids = rows.Select(r => r.p.Id).ToList();
        var qty = await db.StockBatches.AsNoTracking().Where(b => ids.Contains(b.ProductId))
            .GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var items = rows.Select(r => ToDto(r.p, r.Category, qty.GetValueOrDefault(r.p.Id), isAdmin)).ToList();
        return new PagedResult<ProductDto>(items, total, page.SafePage, page.SafeSize);
    }

    public async Task<ProductDto?> ProductAsync(int id, bool isAdmin, CancellationToken ct)
    {
        var r = await db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => new { p, Category = p.Category!.Name }).SingleOrDefaultAsync(ct);
        if (r is null) return null;
        var q = await db.StockBatches.Where(b => b.ProductId == id).SumAsync(b => (int?)b.QuantityOnHand, ct) ?? 0;
        return ToDto(r.p, r.Category, q, isAdmin);
    }

    public async Task<ProductDto?> ProductByBarcodeAsync(string code, bool isAdmin, CancellationToken ct)
    {
        var id = await db.Products.AsNoTracking().Where(p => p.Barcode == code && p.IsActive).Select(p => (int?)p.Id).SingleOrDefaultAsync(ct);
        return id is null ? null : await ProductAsync(id.Value, isAdmin, ct);
    }

    private static ProductDto ToDto(Domain.Entities.Product p, string category, int qty, bool isAdmin) =>
        new(p.Id, p.Sku, p.Name, p.CategoryId, category, p.Unit, p.ReorderLevel, isAdmin ? p.CostPrice : null, p.PriceDistributor,
            p.PriceWholesaler, p.PriceRetail, p.Barcode, p.TracksSerial, p.IsActive, qty,
            p.Species, p.LifeStage, p.ProteinPct, p.FatPct, p.FiberPct, p.MoisturePct, p.NutritionSummary);

    public Task<List<CategoryDto>> CategoriesAsync(CancellationToken ct) =>
        db.Categories.AsNoTracking().OrderBy(c => c.Name).Select(c => new CategoryDto(c.Id, c.Name)).ToListAsync(ct);

    public Task<List<WarehouseDto>> WarehousesAsync(CancellationToken ct) =>
        db.Warehouses.AsNoTracking().OrderBy(w => w.Name).Select(w => new WarehouseDto(w.Id, w.Name, w.Location)).ToListAsync(ct);

    /// <summary>
    /// Stock rows with their status. "Low stock" is a fact about the PRODUCT (everything on hand, all rows and warehouses, at or below its reorder level) — never about one
    /// old row, which would flag a product as low just because one of its emptied rows is at zero. "Expiring soon" (within 60 days) is about the row.
    /// A row with nothing in it is history, not stock, so it is hidden — except the single row of a product that is completely out, which stays as "Out of stock" so you still see what to buy.
    /// Paging is stable: ties on category, name and expiry are broken by the row's id, so every page continues exactly where the previous one stopped.
    /// </summary>
    public async Task<PagedResult<BatchRowDto>> BatchesAsync(PageRequest page, bool onlyLow, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var soon = today.AddDays(60);
        var totals = db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { ProductId = g.Key, Total = g.Sum(b => b.QuantityOnHand) });
        var q = from b in db.StockBatches.AsNoTracking()
                join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                join w in db.Warehouses.AsNoTracking() on b.WarehouseId equals w.Id
                join t in totals on b.ProductId equals t.ProductId
                where b.QuantityOnHand > 0 || (t.Total <= 0 && b.Id == db.StockBatches.Where(x => x.ProductId == b.ProductId).Min(x => x.Id))
                select new { b, p, Category = p.Category!.Name, Warehouse = w.Name, t.Total };
        foreach (var w in SearchTerms.Words(page.Term))
        {
            var day = SearchTerms.Dates(w);
            var from = day?.From; var to = day?.To;
            q = q.Where(x => x.p.Name.Contains(w) || x.p.Sku.Contains(w) || (x.p.Barcode != null && x.p.Barcode.Contains(w)) || x.b.BatchNumber.Contains(w) || x.Category.Contains(w) || x.Warehouse.Contains(w)
                || (from != null && x.b.ExpiryDate != null && x.b.ExpiryDate >= from && x.b.ExpiryDate <= to));
        }
        if (onlyLow) q = q.Where(x => x.Total <= x.p.ReorderLevel || (x.b.QuantityOnHand > 0 && x.b.ExpiryDate != null && x.b.ExpiryDate <= soon));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.Category).ThenBy(x => x.p.Name).ThenBy(x => x.Warehouse).ThenBy(x => x.b.ExpiryDate).ThenBy(x => x.b.Id)
            .Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new PagedResult<BatchRowDto>(rows.Select(x => ToBatch(x.b, x.p, x.Category, x.Warehouse, today, x.Total)).ToList(), total, page.SafePage, page.SafeSize);
    }

    /// <param name="productTotal">Everything on hand of this product across all its rows and warehouses.</param>
    public static BatchRowDto ToBatch(Domain.Entities.StockBatch b, Domain.Entities.Product p, string category, string warehouse, DateOnly today, int productTotal) =>
        new(b.Id, p.Id, p.Sku, p.Name, category, b.WarehouseId, warehouse, b.BatchNumber, b.ExpiryDate, b.QuantityOnHand, p.ReorderLevel,
            productTotal <= 0 ? "Out of stock"
            : productTotal <= p.ReorderLevel ? "Low stock"
            : b.QuantityOnHand > 0 && b.ExpiryDate is { } e && e.DayNumber - today.DayNumber <= 60 ? "Expiring soon" : "OK");

    public async Task<PagedResult<MovementRowDto>> MovementsAsync(PageRequest page, string? referenceType, int? productId, CancellationToken ct)
    {
        var q = from m in db.StockMovements.AsNoTracking()
                join p in db.Products.AsNoTracking() on m.ProductId equals p.Id
                join w in db.Warehouses.AsNoTracking() on m.WarehouseId equals w.Id
                select new { m, p, Warehouse = w.Name };
        if (referenceType is not null) q = q.Where(x => x.m.ReferenceType == referenceType);
        if (productId is not null) q = q.Where(x => x.m.ProductId == productId);
        foreach (var w in SearchTerms.Words(page.Term))
        {
            var span = SearchTerms.Moments(w);
            var from = span?.From; var to = span?.To;
            q = q.Where(x => x.p.Name.Contains(w) || x.p.Sku.Contains(w) || x.Warehouse.Contains(w) || x.m.MovementType.Contains(w) || (x.m.ReferenceType != null && x.m.ReferenceType.Contains(w))
                || (x.m.Note != null && x.m.Note.Contains(w)) || (from != null && x.m.MovementDate >= from && x.m.MovementDate < to));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.m.MovementDate).ThenByDescending(x => x.m.Id)
            .Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        var names = await users.NamesAsync(rows.Select(r => r.m.UserId), ct);
        return new PagedResult<MovementRowDto>(rows.Select(x => new MovementRowDto(x.m.Id, x.m.MovementDate, x.p.Id, x.p.Name, x.p.Sku, x.Warehouse,
            x.m.MovementType, x.m.Quantity, x.m.ReferenceType, x.m.ReferenceId, x.m.UserId, names.GetValueOrDefault(x.m.UserId), x.m.Note)).ToList(), total, page.SafePage, page.SafeSize);
    }
}

public sealed class PartnerQueries(IBusinessDbContext db, IClock clock)
{
    public async Task<PagedResult<SupplierDto>> SuppliersAsync(PageRequest page, CancellationToken ct)
    {
        var q = db.Suppliers.AsNoTracking().AsQueryable();
        foreach (var w in SearchTerms.Words(page.Term))
        {
            var ph = SearchTerms.PhoneNeedle(w);
            q = q.Where(s => s.Name.Contains(w) || (s.ContactName != null && s.ContactName.Contains(w)) || (s.Email != null && s.Email.Contains(w)) || (s.Category != null && s.Category.Contains(w))
                || (s.Address != null && s.Address.Contains(w)) || (s.TaxId != null && s.TaxId.Contains(w)) || (s.Phone != null && s.Phone.Contains(w))
                || (ph != "" && s.Phone != null && s.Phone.Replace(" ", "").Replace("-", "").Replace("+", "").Contains(ph)));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(s => s.Name).ThenBy(s => s.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize)
            .Select(s => new SupplierDto(s.Id, s.Name, s.Category, s.ContactName, s.Phone, s.Email, s.Address, s.TaxId, s.Balance)).ToListAsync(ct);
        return new PagedResult<SupplierDto>(items, total, page.SafePage, page.SafeSize);
    }

    public async Task<PagedResult<CustomerDto>> CustomersAsync(PageRequest page, CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking().AsQueryable();
        foreach (var w in SearchTerms.Words(page.Term)) q = q.Where(CustomerMatches(w));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        var since = clock.BusinessToday.AddMonths(-12);
        var ids = rows.Select(r => r.Id).ToList();
        var spend = await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.CustomerId) && i.InvoiceDate >= since && i.Status != PaymentStatuses.Voided)
            .GroupBy(i => i.CustomerId).Select(g => new { g.Key, S = g.Sum(i => i.TotalAmount) }).ToDictionaryAsync(x => x.Key, x => x.S, ct);
        var items = rows.Select(c =>
        {
            var s = spend.GetValueOrDefault(c.Id);
            return new CustomerDto(c.Id, c.Name, c.CustomerType, c.ContactName, c.Phone, c.Location, c.Address, c.Email, c.TaxId,
                c.RebateRatePct, c.CreditLimit, c.Balance, s, CustomerRanking.For(s));
        }).ToList();
        return new PagedResult<CustomerDto>(items, total, page.SafePage, page.SafeSize);
    }

    /// <summary>One typed word against everything that identifies a customer: name, contact person, phone (however typed), email, place, address, tax id and type.</summary>
    private static System.Linq.Expressions.Expression<Func<Domain.Entities.Customer, bool>> CustomerMatches(string w)
    {
        var ph = SearchTerms.PhoneNeedle(w);
        return c => c.Name.Contains(w) || (c.ContactName != null && c.ContactName.Contains(w)) || (c.Email != null && c.Email.Contains(w)) || (c.Location != null && c.Location.Contains(w))
            || (c.Address != null && c.Address.Contains(w)) || (c.TaxId != null && c.TaxId.Contains(w)) || c.CustomerType.Contains(w) || (c.Phone != null && c.Phone.Contains(w))
            || (ph != "" && c.Phone != null && c.Phone.Replace(" ", "").Replace("-", "").Replace("+", "").Contains(ph));
    }

    /// <summary>Picker data for the sale form (clerks may not open the Customers screen, but they need to choose one).</summary>
    public Task<List<CustomerLookupDto>> LookupAsync(string? search, CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking().AsQueryable();
        foreach (var w in SearchTerms.Words(search)) q = q.Where(CustomerMatches(w));
        return q.OrderBy(c => c.Name).Take(50).Select(c => new CustomerLookupDto(c.Id, c.Name, c.CustomerType, c.Phone, c.Balance)).ToListAsync(ct);
    }
}

public sealed class SalesQueries(IBusinessDbContext db, IUserDirectory users)
{
    public async Task<PagedResult<InvoiceRowDto>> InvoicesAsync(PageRequest page, bool isAdmin, CancellationToken ct)
    {
        var q = from i in db.Invoices.AsNoTracking() join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id select new { i, c.Name, c.Phone, c.Email, c.ContactName };
        foreach (var w in SearchTerms.Words(page.Term))
        {
            var ph = SearchTerms.PhoneNeedle(w);
            var amount = SearchTerms.Amount(w);
            var day = SearchTerms.Dates(w);
            var from = day?.From; var to = day?.To;
            q = q.Where(x => x.i.InvoiceNumber.Contains(w) || x.Name.Contains(w) || (x.ContactName != null && x.ContactName.Contains(w)) || (x.Email != null && x.Email.Contains(w))
                || (x.Phone != null && x.Phone.Contains(w)) || (ph != "" && x.Phone != null && x.Phone.Replace(" ", "").Replace("-", "").Replace("+", "").Contains(ph))
                || x.i.PaymentMethod.Contains(w) || x.i.Status.Contains(w)
                || (amount != null && (x.i.TotalAmount == amount || x.i.AmountPaid == amount))
                || (from != null && x.i.InvoiceDate >= from && x.i.InvoiceDate <= to)
                || db.InvoiceItems.Any(it => it.InvoiceId == x.i.Id && db.Products.Any(pr => pr.Id == it.ProductId && (pr.Name.Contains(w) || pr.Sku.Contains(w)))));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.i.InvoiceDate).ThenByDescending(x => x.i.CreatedAt).ThenByDescending(x => x.i.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        var ids = rows.Select(r => r.i.Id).ToList();
        Dictionary<int, decimal> cogs = [];
        if (isAdmin)
            cogs = await db.InvoiceItems.AsNoTracking().Where(x => ids.Contains(x.InvoiceId)).GroupBy(x => x.InvoiceId)
                .Select(g => new { g.Key, C = g.Sum(x => x.Quantity * x.UnitCost) }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        // X3: estimated profit = Total / (1 + VAT%) − Σ qty×UnitCost. Admin only, and omitted (null) for clerks.
        var items = rows.Select(x => new InvoiceRowDto(x.i.Id, x.i.InvoiceNumber, x.Name, x.i.InvoiceDate, x.i.PaymentMethod, x.i.TotalAmount, x.i.Status,
            isAdmin ? x.i.TotalAmount / (1 + x.i.VatRate / 100m) - cogs.GetValueOrDefault(x.i.Id) : null)).ToList();
        return new PagedResult<InvoiceRowDto>(items, total, page.SafePage, page.SafeSize);
    }

    public async Task<InvoiceDetailDto?> InvoiceAsync(int id, bool isAdmin, CancellationToken ct) =>
        await DetailAsync(db.Invoices.AsNoTracking().Where(i => i.Id == id), isAdmin, ct);

    public async Task<InvoiceDetailDto?> InvoiceByNumberAsync(string number, bool isAdmin, CancellationToken ct) =>
        await DetailAsync(db.Invoices.AsNoTracking().Where(i => i.InvoiceNumber == number), isAdmin, ct);

    private async Task<InvoiceDetailDto?> DetailAsync(IQueryable<Domain.Entities.Invoice> q, bool isAdmin, CancellationToken ct)
    {
        var i = await q.Include(x => x.Items).Include(x => x.Payments).SingleOrDefaultAsync(ct);
        if (i is null) return null;
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == i.CustomerId, ct);
        var productIds = i.Items.Select(x => x.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var names = await users.NamesAsync([i.CreatedByUserId], ct);
        var items = i.Items.OrderBy(x => products[x.ProductId].Name).Select(x => new InvoiceItemDto(x.ProductId, products[x.ProductId].Name,
            products[x.ProductId].Sku, products[x.ProductId].Unit, x.Quantity, x.UnitPrice, x.LineTotal, isAdmin ? x.UnitCost : null)).ToList();
        return new InvoiceDetailDto(i.Id, i.InvoiceNumber, c.Id, c.Name, c.CustomerType, i.InvoiceDate, i.DueDate, i.Subtotal, i.DiscountPct,
            i.DiscountAmount, i.VatRate, i.VatAmount, i.TotalAmount, i.AmountPaid, i.Status, i.PaymentMethod, i.PriceTier, i.WarehouseId,
            names.GetValueOrDefault(i.CreatedByUserId, ""), i.VoidReason, items,
            i.Payments.OrderBy(p => p.PaymentDate).Select(p => new PaymentDto(p.PaymentDate, p.Amount, p.Method, p.BankName, p.AccountName, p.AccountNumber, p.PayerName)).ToList());
    }
}

public sealed class PurchaseQueries(IBusinessDbContext db)
{
    public async Task<PagedResult<PurchaseRowDto>> ListAsync(PageRequest page, CancellationToken ct)
    {
        var q = from p in db.PurchaseOrders.AsNoTracking() join s in db.Suppliers.AsNoTracking() on p.SupplierId equals s.Id select new { p, s.Name, s.Phone };
        foreach (var w in SearchTerms.Words(page.Term))
        {
            var ph = SearchTerms.PhoneNeedle(w);
            var amount = SearchTerms.Amount(w);
            var day = SearchTerms.Dates(w);
            var from = day?.From; var to = day?.To;
            q = q.Where(x => x.p.PoNumber.Contains(w) || x.Name.Contains(w) || x.p.Status.Contains(w) || x.p.PaymentStatus.Contains(w)
                || (x.Phone != null && x.Phone.Contains(w)) || (ph != "" && x.Phone != null && x.Phone.Replace(" ", "").Replace("-", "").Replace("+", "").Contains(ph))
                || (amount != null && (x.p.TotalAmount == amount || x.p.AmountPaid == amount))
                || (from != null && x.p.OrderDate >= from && x.p.OrderDate <= to)
                || db.PurchaseOrderItems.Any(it => it.PurchaseOrderId == x.p.Id && db.Products.Any(pr => pr.Id == it.ProductId && (pr.Name.Contains(w) || pr.Sku.Contains(w)))));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.p.OrderDate).ThenByDescending(x => x.p.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        return new PagedResult<PurchaseRowDto>(rows.Select(x => new PurchaseRowDto(x.p.Id, x.p.PoNumber, x.Name, x.p.OrderDate, x.p.Status,
            x.p.PaymentStatus, x.p.TotalAmount, x.p.AmountPaid)).ToList(), total, page.SafePage, page.SafeSize);
    }

    public async Task<PurchaseDetailDto?> GetAsync(int id, CancellationToken ct)
    {
        var p = await db.PurchaseOrders.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var s = await db.Suppliers.AsNoTracking().SingleAsync(x => x.Id == p.SupplierId, ct);
        var ids = p.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var payments = await db.SupplierPayments.AsNoTracking().Where(x => x.PurchaseOrderId == p.Id).OrderBy(x => x.PaymentDate)
            .Select(x => new SupplierPaymentDto(x.PaymentDate, x.Amount, x.Method, x.BankName, x.AccountName, x.AccountNumber)).ToListAsync(ct);
        return new PurchaseDetailDto(p.Id, p.PoNumber, s.Id, s.Name, p.OrderDate, p.Status, p.PaymentStatus, p.TotalAmount, p.AmountPaid,
            p.Items.Select(i => new PurchaseItemDto(i.ProductId, products[i.ProductId].Name, products[i.ProductId].Sku, i.Quantity, i.UnitCost, i.Quantity * i.UnitCost)).ToList(), payments);
    }
}

public sealed class FinanceQueries(IBusinessDbContext db, IClock clock)
{
    public async Task<FinanceSummaryDto> SummaryAsync(int? year, int? month, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var y = year ?? today.Year; var m = month ?? today.Month;
        var from = new DateOnly(y, m, 1); var to = from.AddMonths(1);
        var live = db.Invoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate < to && i.Status != PaymentStatuses.Voided);
        // X2 (as in vw_ProfitAndLoss): revenue is Σ line totals BEFORE the invoice-level discount — open decision D7, reported separately.
        var revenue = await db.InvoiceItems.AsNoTracking().Where(x => live.Any(i => i.Id == x.InvoiceId)).SumAsync(x => (decimal?)x.LineTotal, ct) ?? 0;
        var cogs = await db.InvoiceItems.AsNoTracking().Where(x => live.Any(i => i.Id == x.InvoiceId)).SumAsync(x => (decimal?)(x.Quantity * x.UnitCost), ct) ?? 0;
        var discounts = await live.SumAsync(i => (decimal?)i.DiscountAmount, ct) ?? 0;
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.ExpenseDate >= from && e.ExpenseDate < to).SumAsync(e => (decimal?)e.Amount, ct) ?? 0;
        var ap = await db.Suppliers.AsNoTracking().Where(s => s.Balance > 0).SumAsync(s => (decimal?)s.Balance, ct) ?? 0;
        var ar = await db.Customers.AsNoTracking().SumAsync(c => (decimal?)c.Balance, ct) ?? 0;
        var rebates = await db.RebateEntries.AsNoTracking().Where(r => r.Status == "Accrued").SumAsync(r => (decimal?)r.Amount, ct) ?? 0;
        return new FinanceSummaryDto(y, m, revenue, discounts, cogs, revenue - cogs, expenses, revenue - cogs - expenses, ap, ar, rebates);
    }

    public async Task<List<MonthlyIncomeDto>> MonthlyIncomeAsync(int year, CancellationToken ct)
    {
        var from = new DateOnly(year, 1, 1); var to = from.AddYears(1);
        var rows = await db.Invoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate < to && i.Status != PaymentStatuses.Voided)
            .Select(i => new { i.InvoiceDate, i.TotalAmount, i.VatAmount, i.AmountPaid }).ToListAsync(ct);
        return rows.GroupBy(r => r.InvoiceDate.Month).OrderBy(g => g.Key)
            .Select(g => new MonthlyIncomeDto(year, g.Key, g.Count(), g.Sum(r => r.TotalAmount), g.Sum(r => r.VatAmount),
                g.Sum(r => r.TotalAmount - r.VatAmount), g.Sum(r => r.AmountPaid), g.Sum(r => r.TotalAmount - r.AmountPaid))).ToList();
    }

    public async Task<PagedResult<LedgerRowDto>> LedgerAsync(PageRequest page, string? accountType, CancellationToken ct)
    {
        var q = db.Ledger.AsNoTracking().AsQueryable();
        if (accountType is not null) q = q.Where(l => l.AccountType == accountType);
        foreach (var w in SearchTerms.Words(page.Term))
        {
            var amount = SearchTerms.Amount(w);
            var day = SearchTerms.Dates(w);
            var from = day?.From; var to = day?.To;
            q = q.Where(l => l.AccountName.Contains(w) || (l.Reference != null && l.Reference.Contains(w)) || l.EntryType.Contains(w) || (amount != null && l.Amount == amount) || (from != null && l.EntryDate >= from && l.EntryDate <= to));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(l => l.EntryDate).ThenByDescending(l => l.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize)
            .Select(l => new LedgerRowDto(l.Id, l.EntryDate, l.AccountName, l.AccountType, l.EntryType, l.Amount, l.Reference)).ToListAsync(ct);
        return new PagedResult<LedgerRowDto>(items, total, page.SafePage, page.SafeSize);
    }
}

public sealed class DashboardQueries(IBusinessDbContext db, IClock clock, FinanceQueries finance, SalesQueries sales)
{
    public async Task<DashboardDto> GetAsync(bool isAdmin, CancellationToken ct)
    {
        var today = clock.BusinessToday;
        var stockValue = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                                select (decimal?)(b.QuantityOnHand * p.CostPrice)).SumAsync(ct) ?? 0;
        // One entry per LOW PRODUCT (total on hand across all its rows at or below its reorder level), shown on the row holding most of it — not one entry per old row.
        var totals = db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { ProductId = g.Key, Total = g.Sum(b => b.QuantityOnHand) });
        var low = await (from b in db.StockBatches.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                         join w in db.Warehouses.AsNoTracking() on b.WarehouseId equals w.Id
                         join t in totals on b.ProductId equals t.ProductId
                         where p.IsActive && t.Total <= p.ReorderLevel
                         select new { b, p, Category = p.Category!.Name, Warehouse = w.Name, t.Total }).ToListAsync(ct);
        var lowRows = low.GroupBy(x => x.p.Id).Select(g => g.OrderByDescending(x => x.b.QuantityOnHand).ThenBy(x => x.b.Id).First())
            .OrderBy(x => x.p.Name).Take(100).Select(x => CatalogQueries.ToBatch(x.b, x.p, x.Category, x.Warehouse, today, x.Total)).ToList();
        var todays = await db.Invoices.AsNoTracking().CountAsync(i => i.InvoiceDate == today && i.Status != PaymentStatuses.Voided, ct);
        var recent = (await sales.InvoicesAsync(new PageRequest(1, 10), isAdmin: false, ct)).Items;

        if (!isAdmin) return new DashboardDto(stockValue, lowRows.Count, todays, recent, lowRows, null, null, null);

        var fin = await finance.SummaryAsync(null, null, ct);
        var since = clock.UtcNow.AddDays(-RestockAdvisor.DemandWindowDays);
        var sold = await db.StockMovements.AsNoTracking().Where(m => m.MovementType == MovementTypes.Out && m.ReferenceType != MovementReferences.Transfer && m.MovementDate >= since)
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, S = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.S, ct);
        var onHand = await db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        var advice = products.Select(p => (p, a: RestockAdvisor.Advise(onHand.GetValueOrDefault(p.Id), p.ReorderLevel, sold.GetValueOrDefault(p.Id))))
            .Where(x => x.a.Urgency != "OK")
            .OrderBy(x => x.a.Urgency == "Out of stock" ? 0 : x.a.Urgency == "Reorder now" ? 1 : 2)
            .Select(x => new ReorderAdviceDto(x.p.Id, x.p.Name, x.a.OnHand, x.a.ReorderLevel, x.a.AvgDailyDemand, x.a.DaysOfCover, x.a.ReorderPoint,
                x.a.SuggestedOrderQty, x.a.Urgency, x.a.Summary)).ToList();
        return new DashboardDto(stockValue, lowRows.Count, todays, recent, lowRows, fin.GrossProfit, fin.Expenses, advice);
    }
}
