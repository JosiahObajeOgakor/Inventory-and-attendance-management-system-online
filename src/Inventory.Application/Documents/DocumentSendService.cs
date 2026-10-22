using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Domain.Entities;

namespace Inventory.Application.Documents;

public sealed record DocumentSent(string Channel, string To, string File);

/// <summary>
/// Sends a sale receipt to the customer, or a purchase order to the supplier, as a PDF over the business's WhatsApp number. Blank number = the one
/// on their record. Meta only delivers a free-form document to someone who has written to the business in the last 24 hours, so the screen also
/// offers Share (the person's own WhatsApp) for everyone else. Every send is audited.
/// </summary>
public sealed class DocumentSendService(DocumentQueries docs, IDocumentRenderer renderer, IWhatsAppSender whatsapp, IBusinessDbContext db, ICompanyContext company, IClock clock)
{
    public bool WhatsAppReady => whatsapp.IsConfigured(company.Key);

    public async Task<DocumentSent> ReceiptToWhatsAppAsync(int invoiceId, string? phone, CurrentUser user, CancellationToken ct)
    {
        var d = await docs.ReceiptAsync(invoiceId, ct);
        var to = Number(phone, d.Customer.Phone, "customer");
        var file = $"Receipt-{d.Number}.pdf";
        await whatsapp.SendDocumentAsync(company.Key, to, renderer.Receipt(d), file, EmailTemplates.ReceiptCaption(d), ct);
        await AuditAsync(user, "RECEIPT_WHATSAPPED", "Invoice", invoiceId, $"{d.Number} to +{to}", ct);
        return new DocumentSent("whatsapp", "+" + to, file);
    }

    public async Task<DocumentSent> PurchaseToWhatsAppAsync(int purchaseOrderId, string? phone, CurrentUser user, CancellationToken ct)
    {
        var d = await docs.PurchaseOrderAsync(purchaseOrderId, ct);
        var to = Number(phone, d.Supplier.Phone, "supplier");
        var file = $"Purchase-order-{d.Number}.pdf";
        await whatsapp.SendDocumentAsync(company.Key, to, renderer.PurchaseOrder(d), file, EmailTemplates.PurchaseCaption(d), ct);
        await AuditAsync(user, "PURCHASE_WHATSAPPED", "PurchaseOrder", purchaseOrderId, $"{d.Number} to +{to}", ct);
        return new DocumentSent("whatsapp", "+" + to, file);
    }

    public async Task<DocumentSent> SupplyToWhatsAppAsync(int supplyId, string? phone, CurrentUser user, CancellationToken ct)
    {
        var d = await docs.SupplyAsync(supplyId, ct);
        var to = Number(phone, d.Supplier.Phone, "supplier");
        var file = $"Purchase-{d.Number}.pdf";
        await whatsapp.SendDocumentAsync(company.Key, to, renderer.Supply(d), file, EmailTemplates.SupplyCaption(d), ct);
        await AuditAsync(user, "SUPPLY_WHATSAPPED", "Supply", supplyId, $"{d.Number} to +{to}", ct);
        return new DocumentSent("whatsapp", "+" + to, file);
    }

    private string Number(string? typed, string? onRecord, string who)
    {
        if (!WhatsAppReady) throw new BusinessRuleException("WhatsApp isn't set up for this business on the server. Use Share instead.");
        var raw = string.IsNullOrWhiteSpace(typed) ? onRecord : typed;
        if (string.IsNullOrWhiteSpace(raw) || raw.Count(char.IsDigit) < 10) throw new BusinessRuleException($"Enter the {who}'s WhatsApp number.");
        return OrderDispatchService.International(raw);
    }

    private async Task AuditAsync(CurrentUser u, string action, string entity, int id, string detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog { UserId = u.Id, UserName = u.FullName, Action = action, Entity = entity, EntityId = id.ToString(), At = clock.UtcNow, Detail = detail });
        await db.SaveChangesAsync(ct);
    }
}
