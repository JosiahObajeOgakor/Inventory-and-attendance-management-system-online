using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Payments;
using Inventory.Application.Queries;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Documents;

/// <summary>Assembles the data for each printable document from the database. Read-only.</summary>
public sealed class DocumentQueries(IBusinessDbContext db, CompanyProfileService profile, ICompanyContext company, IUserDirectory users, IClock clock, PaymentLinkService pay)
{
    public async Task<Branding> BrandAsync(CancellationToken ct)
    {
        var p = await profile.EnsureAsync(ct);
        var assets = await db.CompanyAssets.AsNoTracking().ToDictionaryAsync(a => a.Kind, a => a.Data, ct);
        return new Branding(company.Key, p.LegalName, p.Address, p.Phone, p.Email, p.TaxId,
            assets.GetValueOrDefault(AssetKinds.Logo), assets.GetValueOrDefault(AssetKinds.Signature), assets.GetValueOrDefault(AssetKinds.WaybillStamp),
            p.Banks.OrderBy(b => b.SortOrder).Select(b => new BankInfo(b.BankName, b.AccountName, b.AccountNumber)).ToList());
    }

    /// <summary>A clerk login is shared front-desk staff, not one named person, so their documents show "&lt;Company&gt; (Clerk)"; only an admin's own name is printed.</summary>
    private string PersonOn(UserBrief? u, string companyName) =>
        u is null ? "" : u.Role is RoleNames.Clerk or RoleNames.LegacyClerk ? $"{companyName} (Clerk)" : u.FullName;

    private static string OneLine(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static PartyInfo Party(Customer c) =>
        new(c.Name, c.ContactName ?? "", OneLine(c.Address, c.Location), c.Phone ?? "", c.Email ?? "", c.TaxId ?? "", c.CustomerType);

    public async Task<ReceiptDoc> ReceiptAsync(int invoiceId, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == i.CustomerId, ct);
        var brand = await BrandAsync(ct);
        var wh = i.WarehouseId is int w ? await db.Warehouses.AsNoTracking().Where(x => x.Id == w).Select(x => x.Name).SingleOrDefaultAsync(ct) ?? "" : "";
        var ids = i.Items.Select(x => x.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = i.Items.OrderBy(x => products[x.ProductId].Name).Select((x, n) =>
            new DocLine(n + 1, products[x.ProductId].Sku, products[x.ProductId].Name, products[x.ProductId].Unit, x.Quantity, x.UnitPrice, x.LineTotal)).ToList();
        var rebate = await db.RebateEntries.AsNoTracking().Where(r => r.CustomerId == c.Id && r.Status == "Accrued").SumAsync(r => (decimal?)r.Amount, ct) ?? 0;
        var briefs = await users.BriefsAsync([i.CreatedByUserId], ct);
        var bal = i.TotalAmount - i.AmountPaid;
        // What this customer owes on OTHER invoices: their running balance minus this invoice's own share.
        var elsewhere = Math.Max(0m, c.Balance - bal);
        var payUrl = i.Status == PaymentStatuses.Voided ? null : await PayUrlAsync(PaymentDocTypes.Invoice, i.Id, i.InvoiceNumber, bal, c, ct);
        return new ReceiptDoc(brand, i.InvoiceNumber, i.InvoiceDate, i.Status, bal > 0 ? i.DueDate : null, Party(c), i.PriceTier, wh, i.PaymentMethod,
            PersonOn(briefs.GetValueOrDefault(i.CreatedByUserId), brand.Name), lines, i.Subtotal, i.DiscountPct, i.DiscountAmount, i.VatRate, i.VatAmount,
            i.TotalAmount, i.AmountPaid, i.Status == PaymentStatuses.Voided ? 0m : elsewhere, rebate, clock.BusinessNow, payUrl);
    }

    /// <summary>The Paystack link for this document at its exact amount. A payment-provider problem must never stop a document from printing, so failures give no link rather than an error.</summary>
    private async Task<string?> PayUrlAsync(string docType, int docId, string number, decimal amount, Customer c, CancellationToken ct)
    {
        try { return (await pay.GetOrCreateAsync(docType, docId, number, amount, c.Email, c.Name, ct))?.Url; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task<QuotationDoc> QuotationAsync(int id, CancellationToken ct)
    {
        var q = await db.Quotations.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Quotation");
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == q.CustomerId, ct);
        var brand = await BrandAsync(ct);
        var ids = q.Items.Select(x => x.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = q.Items.OrderBy(x => products[x.ProductId].Name).Select((x, n) =>
            new DocLine(n + 1, products[x.ProductId].Sku, products[x.ProductId].Name, products[x.ProductId].Unit, x.Quantity, x.UnitPrice, x.LineTotal)).ToList();
        var briefs = await users.BriefsAsync([q.CreatedByUserId], ct);
        var payUrl = q.Status == QuotationStatuses.Open ? await PayUrlAsync(PaymentDocTypes.Quotation, q.Id, q.QuotationNumber, q.TotalAmount, c, ct) : null;
        return new QuotationDoc(brand, q.QuotationNumber, q.QuotationDate, q.Status, Party(c), q.PriceTier, PersonOn(briefs.GetValueOrDefault(q.CreatedByUserId), brand.Name),
            lines, q.Subtotal, q.DiscountPct, q.DiscountAmount, q.VatRate, q.VatAmount, q.TotalAmount, clock.BusinessNow, payUrl);
    }

    public async Task<WaybillDoc> WaybillAsync(int id, CancellationToken ct)
    {
        var w = await db.Waybills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Waybill");
        var i = await db.Invoices.AsNoTracking().Include(x => x.Items).SingleAsync(x => x.Id == w.InvoiceId, ct);
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == i.CustomerId, ct);
        var brand = await BrandAsync(ct);
        var wh = i.WarehouseId is int wid ? await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == wid, ct) : null;
        var ids = i.Items.Select(x => x.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = i.Items.OrderBy(x => products[x.ProductId].Name).Select((x, n) =>
            new DocLine(n + 1, products[x.ProductId].Sku, products[x.ProductId].Name, products[x.ProductId].Unit, x.Quantity, 0, 0)).ToList();
        var briefs = w.CreatedByUserId is int uid ? await users.BriefsAsync([uid], ct) : [];
        return new WaybillDoc(brand, w.WaybillNumber, w.IssueDate, i.InvoiceNumber, i.InvoiceDate, Party(c), w.DestinationAddress ?? "",
            OneLine(wh?.Name, wh?.Location), PersonOn(w.CreatedByUserId is int u2 ? briefs.GetValueOrDefault(u2) : null, brand.Name),
            w.DriverName ?? "", w.DriverPhone ?? "", w.VehiclePlate ?? "", w.Notes ?? "", lines, clock.BusinessNow);
    }

    public async Task<PriceListDoc> PriceListAsync(string tier, string? customerName, bool includeOutOfStock, CancellationToken ct)
    {
        if (!PriceTiers.IsValid(tier)) throw new BusinessRuleException("Unknown price list.");
        var brand = await BrandAsync(ct);
        var qty = await db.StockBatches.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).Select(p => new { p, Category = p.Category!.Name }).ToListAsync(ct);
        var lines = products.Where(x => includeOutOfStock || qty.GetValueOrDefault(x.p.Id) > 0)
            .OrderBy(x => x.Category).ThenBy(x => x.p.Name)
            .Select(x => new PriceListLine(x.Category, x.p.Name, x.p.Sku, x.p.Unit, x.p.PriceFor(tier), qty.GetValueOrDefault(x.p.Id))).ToList();
        var today = clock.BusinessToday;
        return new PriceListDoc(brand, tier, customerName, $"PL-{today:yyyyMMdd}", today, lines, clock.BusinessNow);
    }
}
