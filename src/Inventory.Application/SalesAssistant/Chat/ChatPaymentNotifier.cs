using Inventory.Application.Abstractions;
using Inventory.Application.Messaging;
using Inventory.Application.Payments;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// The other half of "never leave the WhatsApp chat": when the bank webhook settles a payment, this pushes the confirmation (or the "you're short" /
/// "paid but out of stock" notice) into the same chat, in the language the customer has been using. The sale itself is already recorded by
/// <see cref="PaymentLinkService.SettleAsync"/>; a failure to send here is logged and never undoes it — the customer can also just message "paid".
/// </summary>
public sealed class ChatPaymentNotifier(IBusinessDbContext db, ChatOrderText texts, IChatSender sender, ICompanyContext company, IClock clock, ILogger<ChatPaymentNotifier> log)
{
    public async Task NotifyAsync(SettleResult r, CancellationToken ct)
    {
        if (r.Reference is null) return;
        var link = await db.PaymentLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Reference == r.Reference, ct);
        if (link is null || link.DocType != PaymentDocTypes.Quotation) return;      // bank-account (AlatPay) and card / USSD link (Paystack) orders alike
        var customerId = await db.Quotations.AsNoTracking().Where(q => q.Id == link.DocId).Select(q => (int?)q.CustomerId).FirstOrDefaultAsync(ct);
        var rawPhone = customerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Phone).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(rawPhone)) return;
        var phone = CustomerLookupService.Normalize(rawPhone);

        // The conversation that placed THIS order (WhatsApp or the website's chat); otherwise the customer's WhatsApp chat.
        var conv = await db.ChatConversations.FirstOrDefaultAsync(c => c.QuotationId == link.DocId, ct)
                   ?? await db.ChatConversations.FirstOrDefaultAsync(c => c.Channel == ChatChannels.WhatsApp && c.ExternalId == phone, ct);
        var lang = conv?.Language ?? ChatLanguages.English;

        string text;
        if (r.Applied) text = await texts.PaidAsync(lang, link.DocId, r.Paid ?? link.Amount, r.InvoiceNumber, ct);
        else if (r.Unfulfilled) text = await texts.UnfulfilledAsync(lang, link.DocId, r.Paid ?? link.Amount, ct);
        else if (r.Shortfall is not null) text = ChatOrderText.Underpaid(lang, r.Paid ?? 0, link.Amount, link.BankName ?? "", link.AccountNumber ?? "");
        else return;

        if (conv is not null)
        {
            if ((r.Applied || r.Unfulfilled) && conv.QuotationId == link.DocId)
            {
                // Only reset the chat if it is still on THIS order; if the customer already started another one, leave it alone.
                conv.State = ChatStates.Idle; conv.QuotationId = null; conv.DraftJson = null; conv.AiCalls = 0;
            }
            db.ChatMessages.Add(new ChatLogMessage { ConversationId = conv.Id, Role = ChatRoles.Assistant, Text = text, CreatedAt = clock.UtcNow });
            conv.LastMessageAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // A website buyer sees the message on the page (the widget checks for new messages while a payment is outstanding); only WhatsApp gets a push.
        if (conv is not null && conv.Channel != ChatChannels.WhatsApp) return;
        if (!sender.IsConfigured(company.Key)) { log.LogWarning("Payment settled but no chat sender is configured; the customer was not messaged"); return; }
        try { await sender.SendTextAsync(company.Key, phone, text, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not push the payment confirmation into the chat"); }
    }
}
