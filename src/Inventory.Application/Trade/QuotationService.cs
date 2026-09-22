using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Queries;
using Inventory.Application.Sales;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Trade;

public sealed class QuoteRequest
{
    public int CustomerId { get; set; }
    public DateOnly? QuoteDate { get; set; }
    public string PriceTier { get; set; } = PriceTiers.Retailer;
    public decimal DiscountPct { get; set; }
    public decimal VatRate { get; set; }
    public List<SaleLineDto> Lines { get; set; } = [];
}

public sealed record QuoteResult(int Id, string Number, decimal Subtotal, decimal DiscountAmount, decimal VatAmount, decimal Total);

/// <summary>The sale details filled in when a quotation is turned into a real sale (its customer, prices, discount and VAT come from the quote).</summary>
public sealed class ConvertQuoteRequest
{
    public int WarehouseId { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public decimal PaidNow { get; set; }
    public DateOnly? DueDate { get; set; }
}

public sealed record QuotationRowDto(int Id, string QuotationNumber, string Customer, DateOnly QuotationDate, decimal TotalAmount, string Status, string PreparedBy, int? ConvertedInvoiceId);
public sealed record QuotationItemDto(int ProductId, string Product, string Sku, string Unit, int Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record QuotationDetailDto(int Id, string QuotationNumber, int CustomerId, string Customer, string CustomerType, DateOnly QuotationDate, decimal Subtotal,
    decimal DiscountPct, decimal DiscountAmount, decimal VatRate, decimal VatAmount, decimal TotalAmount, string PriceTier, string Status, int? ConvertedInvoiceId,
    string PreparedBy, IReadOnlyList<QuotationItemDto> Items);

public sealed class QuoteRequestValidator : AbstractValidator<QuoteRequest>
{
    public QuoteRequestValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThan(0);
        RuleFor(x => x.PriceTier).Must(PriceTiers.IsValid).WithMessage("Unknown price tier.");
        RuleFor(x => x.DiscountPct).InclusiveBetween(0, 100);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one product line.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).GreaterThan(0);
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>Rule Q1: a price computation that never touches stock, ledger or balance until it is converted into a sale.</summary>
public sealed class QuotationService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, SalesService sales, IUserDirectory users)
{
    public async Task<QuoteResult> CreateAsync(QuoteRequest req, CurrentUser user, CancellationToken ct = default)
    {
        await new QuoteRequestValidator().ValidateAndThrowAsync(req, ct);
        return await tx.RunAsync(async inner =>
        {
            if (!await db.Customers.AnyAsync(c => c.Id == req.CustomerId, inner)) throw new NotFoundException("Customer");
            var ids = req.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, inner);
            if (products.Count != ids.Count || products.Values.Any(p => !p.IsActive)) throw new BusinessRuleException("One of the products on this quotation no longer exists or is inactive.");

            var totals = DocumentCalculator.Sale(req.Lines.Select(l => new SaleLineInput(l.ProductId, l.Quantity, l.UnitPrice)), req.DiscountPct, req.VatRate);
            var number = await DocumentNumbers.NextAsync(company, clock, (n, c) => db.Quotations.AnyAsync(q => q.QuotationNumber == n, c), inner);
            var q = new Quotation
            {
                QuotationNumber = number, CustomerId = req.CustomerId, QuotationDate = req.QuoteDate ?? clock.BusinessToday, Subtotal = totals.Subtotal,
                DiscountPct = req.DiscountPct, DiscountAmount = totals.DiscountAmount, VatRate = req.VatRate, VatAmount = totals.VatAmount, TotalAmount = totals.Total,
                PriceTier = req.PriceTier, Status = QuotationStatuses.Open, CreatedByUserId = user.Id, CreatedAt = clock.UtcNow,
                Items = req.Lines.Select(l => new QuotationItem { ProductId = l.ProductId, Quantity = l.Quantity, UnitPrice = l.UnitPrice, LineTotal = l.Quantity * l.UnitPrice }).ToList(),
            };
            db.Quotations.Add(q);
            foreach (var l in req.Lines)
            {
                var p = products[l.ProductId];
                var standard = p.PriceFor(req.PriceTier);
                if (standard != l.UnitPrice)
                    db.PriceOverrides.Add(new PriceOverride { DocType = "Quotation", DocNumber = number, ProductName = p.Name, StandardPrice = standard, OverridePrice = l.UnitPrice, ChangedByName = user.FullName, ChangedAt = clock.UtcNow });
            }
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "QUOTATION_CREATED", Entity = "Quotation", At = clock.UtcNow, Detail = number });
            await db.SaveChangesAsync(inner);
            return new QuoteResult(q.Id, number, totals.Subtotal, totals.DiscountAmount, totals.VatAmount, totals.Total);
        }, ct);
    }

    /// <summary>
    /// Turns an Open quotation into a real sale in ONE transaction. The desktop app did it in two separate steps, so a failure in
    /// the second left an invoice while the quote still read Open. Here, if the sale is refused (say, not enough stock) the quote stays Open and nothing else changes.
    /// </summary>
    public Task<SaleResult> ConvertAsync(int id, ConvertQuoteRequest req, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var q = await db.Quotations
                .FromSqlInterpolated($"SELECT * FROM quotations WHERE Id = {id} FOR UPDATE")
                .Include(x => x.Items)
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Quotation");
            if (q.Status != QuotationStatuses.Open) throw new BusinessRuleException("This quotation has already been converted to a sale.");

            var sale = new SaleRequest
            {
                CustomerId = q.CustomerId, PriceTier = q.PriceTier, WarehouseId = req.WarehouseId, PaymentMethod = req.PaymentMethod, DiscountPct = q.DiscountPct,
                VatRate = q.VatRate, PaidNow = req.PaidNow, DueDate = req.DueDate,
                Lines = q.Items.Select(i => new SaleLineDto { ProductId = i.ProductId, Quantity = i.Quantity, UnitPrice = i.UnitPrice }).ToList(),
            };
            var result = await sales.SaveWithinAsync(sale, user, null, inner);
            q.Status = QuotationStatuses.Converted;
            q.ConvertedInvoiceId = result.InvoiceId;
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "QUOTATION_CONVERTED", Entity = "Quotation", EntityId = q.Id.ToString(), At = clock.UtcNow, Detail = $"{q.QuotationNumber} -> {result.InvoiceNumber}" });
            await db.SaveChangesAsync(inner);
            return result;
        }, ct);

    /// <summary>Only an Open quotation can be deleted (Admin).</summary>
    public Task DeleteAsync(int id, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            var q = await db.Quotations
                .FromSqlInterpolated($"SELECT * FROM quotations WHERE Id = {id} FOR UPDATE")
                .Include(x => x.Items)
                .SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Quotation");
            if (q.Status != QuotationStatuses.Open) throw new BusinessRuleException("A converted quotation is part of a sale and can't be deleted.");
            db.QuotationItems.RemoveRange(q.Items);
            db.Quotations.Remove(q);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "QUOTATION_DELETED", Entity = "Quotation", EntityId = id.ToString(), At = clock.UtcNow, Detail = q.QuotationNumber });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    private static string PersonOn(UserBrief? u, string companyName) =>
        u is null ? "" : u.Role is RoleNames.Clerk or RoleNames.LegacyClerk ? $"{companyName} (Clerk)" : u.FullName;

    public async Task<PagedResult<QuotationRowDto>> ListAsync(PageRequest page, CancellationToken ct)
    {
        var q = from x in db.Quotations.AsNoTracking() join c in db.Customers.AsNoTracking() on x.CustomerId equals c.Id select new { x, c.Name };
        if (page.Term is { } t) q = q.Where(r => r.x.QuotationNumber.Contains(t) || r.Name.Contains(t) || r.x.Status.Contains(t));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.x.Id).Skip((page.SafePage - 1) * page.SafeSize).Take(page.SafeSize).ToListAsync(ct);
        var briefs = await users.BriefsAsync(rows.Select(r => r.x.CreatedByUserId), ct);
        return new PagedResult<QuotationRowDto>(rows.Select(r => new QuotationRowDto(r.x.Id, r.x.QuotationNumber, r.Name, r.x.QuotationDate, r.x.TotalAmount, r.x.Status,
            PersonOn(briefs.GetValueOrDefault(r.x.CreatedByUserId), company.LegalName), r.x.ConvertedInvoiceId)).ToList(), total, page.SafePage, page.SafeSize);
    }

    public async Task<QuotationDetailDto?> GetAsync(int id, CancellationToken ct)
    {
        var q = await db.Quotations.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (q is null) return null;
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == q.CustomerId, ct);
        var ids = q.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var briefs = await users.BriefsAsync([q.CreatedByUserId], ct);
        return new QuotationDetailDto(q.Id, q.QuotationNumber, c.Id, c.Name, c.CustomerType, q.QuotationDate, q.Subtotal, q.DiscountPct, q.DiscountAmount, q.VatRate, q.VatAmount,
            q.TotalAmount, q.PriceTier, q.Status, q.ConvertedInvoiceId, PersonOn(briefs.GetValueOrDefault(q.CreatedByUserId), company.LegalName),
            q.Items.OrderBy(i => products[i.ProductId].Name).Select(i => new QuotationItemDto(i.ProductId, products[i.ProductId].Name, products[i.ProductId].Sku, products[i.ProductId].Unit, i.Quantity, i.UnitPrice, i.LineTotal)).ToList());
    }
}
