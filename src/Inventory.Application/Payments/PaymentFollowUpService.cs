using Inventory.Application.Abstractions;
using Inventory.Application.Documents;
using Inventory.Application.Messaging;
using Inventory.Application.SalesAssistant;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Payments;

/// <summary>
/// After a payment settles, tries to hand the customer their receipt over WhatsApp. Kept as a sibling of
/// PaymentLinkService (not called from inside it) to avoid a DI cycle: DocumentQueries already depends on
/// PaymentLinkService (for the PayUrl printed on documents), so PaymentLinkService cannot depend back on
/// anything that reaches DocumentQueries. Never allowed to affect the money already recorded — any failure
/// here is logged and swallowed, not thrown back at the caller.
/// </summary>
public sealed class PaymentFollowUpService(DocumentQueries docs, IDocumentRenderer renderer, IWhatsAppSender whatsapp, ICompanyContext company, ILogger<PaymentFollowUpService> log)
{
    public async Task SendReceiptIfPossibleAsync(SettleResult result, CancellationToken ct)
    {
        if (!result.Applied || result.InvoiceId is not int invoiceId || string.IsNullOrWhiteSpace(result.CustomerPhone)) return;
        if (!whatsapp.IsConfigured(company.Key)) return;
        try
        {
            var doc = await docs.ReceiptAsync(invoiceId, ct);
            var pdf = renderer.Receipt(doc);
            var to = CustomerLookupService.Normalize(result.CustomerPhone);
            await whatsapp.SendDocumentAsync(company.Key, to, pdf, $"Receipt-{doc.Number}.pdf", "Thank you for your order! Here's your receipt.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not send WhatsApp receipt for invoice {InvoiceId}", invoiceId);
        }
    }
}
