using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Documents;

/// <summary>
/// What to put in a price list / catalog for a customer: the price tier (distributor, wholesale, retail — or the customer's own), all items or
/// just the chosen ones, with photos (catalog) or as a plain table, and as a PDF or one PNG image.
/// </summary>
public sealed record CatalogRequest(int? CustomerId, string? Tier, IReadOnlyList<int>? ProductIds, bool Catalog = true, string Format = "pdf", bool IncludeOutOfStock = false);

public sealed record CatalogFile(byte[] Bytes, string FileName, string ContentType, string Caption);
public sealed record CatalogSent(string Channel, string To, string File);

/// <summary>Builds the price list / catalog and hands it over: as a download, by email (from the business mailbox) or on WhatsApp. Every send is audited.</summary>
public sealed class CatalogService(DocumentQueries docs, IDocumentRenderer renderer, DocumentEmailService email, IWhatsAppSender whatsapp,
    IBusinessDbContext db, ICompanyContext company, IClock clock)
{
    private async Task<(PriceListDoc Doc, Customer? Customer)> BuildAsync(CatalogRequest r, CancellationToken ct)
    {
        var c = r.CustomerId is int id ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer") : null;
        var tier = PriceTiers.IsValid(r.Tier) ? r.Tier! : c?.CustomerType switch { "Distributor" => PriceTiers.Distributor, "Wholesaler" => PriceTiers.Wholesaler, _ => PriceTiers.Retailer };
        if (r.ProductIds is { Count: > 500 }) throw new BusinessRuleException("Choose up to 500 products at a time.");
        var doc = await docs.PriceListAsync(tier, c?.Name, r.IncludeOutOfStock, r.ProductIds?.Distinct().ToList(), r.Catalog, ct);
        if (doc.Lines.Count == 0) throw new BusinessRuleException("There is nothing to list: no product in stock matches. Tick \"include out of stock\" or choose items.");
        return (doc, c);
    }

    public async Task<CatalogFile> FileAsync(CatalogRequest r, CancellationToken ct) => Render((await BuildAsync(r, ct)).Doc, r.Format);

    private CatalogFile Render(PriceListDoc d, string? format)
    {
        var png = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);
        var name = $"{(d.Catalog ? "Catalog" : "Price-list")}-{d.Tier}-{d.Date:yyyy-MM-dd}";
        var caption = $"{d.Brand.Name} — {(d.Catalog ? "product catalog" : "price list")} ({(d.Tier == PriceTiers.Wholesaler ? "wholesale" : d.Tier == PriceTiers.Retailer ? "retail" : "distributor")} prices), {d.Lines.Count} item(s)";
        return png ? new CatalogFile(renderer.PriceListPng(d), name + ".png", "image/png", caption) : new CatalogFile(renderer.PriceList(d), name + ".pdf", "application/pdf", caption);
    }

    public async Task<CatalogSent> EmailAsync(CatalogRequest r, string? to, string? note, CurrentUser user, CancellationToken ct)
    {
        var (d, c) = await BuildAsync(r, ct);
        var f = Render(d, r.Format);
        var sent = await email.SendPriceListFileAsync(d, f.Bytes, f.FileName, f.ContentType, string.IsNullOrWhiteSpace(to) ? c?.Email : to, note, user, ct);
        return new CatalogSent("email", sent.To, sent.Attachment);
    }

    /// <summary>
    /// Sends it on the business WhatsApp number. Meta only delivers to someone who messaged the business in the last 24 hours, so this is for
    /// customers already chatting with us; the screen also offers "share" from the phone, which always works.
    /// </summary>
    public async Task<CatalogSent> WhatsAppAsync(CatalogRequest r, string? phone, CurrentUser user, CancellationToken ct)
    {
        if (!whatsapp.IsConfigured(company.Key)) throw new BusinessRuleException("WhatsApp isn't set up for this business on the server. Use Share instead.");
        var (d, c) = await BuildAsync(r, ct);
        var raw = string.IsNullOrWhiteSpace(phone) ? c?.Phone : phone;
        if (string.IsNullOrWhiteSpace(raw) || raw.Count(char.IsDigit) < 10) throw new BusinessRuleException("Enter the customer's WhatsApp number.");
        var to = OrderDispatchService.International(raw);
        var f = Render(d, r.Format);
        if (f.ContentType == "image/png") await whatsapp.SendImageAsync(company.Key, to, f.Bytes, f.Caption, ct);
        else await whatsapp.SendDocumentAsync(company.Key, to, f.Bytes, f.FileName, f.Caption, ct);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "CATALOG_WHATSAPPED", Entity = "PriceList", At = clock.UtcNow, Detail = $"{f.FileName} to +{to}" });
        await db.SaveChangesAsync(ct);
        return new CatalogSent("whatsapp", "+" + to, f.FileName);
    }
}
