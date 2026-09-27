using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Options;
using AiChatMessage = Inventory.Application.Ai.ChatMessage;

namespace Inventory.Application.SalesAssistant;

/// <param name="PaymentOptions">When not empty, the channel should offer these providers as tap-to-choose buttons (an order was just created or the customer must choose).</param>
public sealed record SalesAssistantReply(string Text, string? CheckoutUrl, string? QuotationNumber, bool Limited, IReadOnlyList<string>? PaymentOptions = null, decimal? OrderTotal = null);

/// <summary>
/// The customer-facing sales assistant: same tool-calling shape as the admin AssistantService, but its
/// own persona/tool-set, DB-persisted history (not client-supplied), and a per-conversation rate limit
/// instead of a per-user one.
/// </summary>
public sealed class SalesAssistantService(
    IChatModel model, SalesAssistantToolbox tools, ChatConversationService conversations,
    ICompanyContext company, IClock clock, IOptions<SalesAssistantOptions> options)
{
    private SalesAssistantOptions Opt => options.Value;

    public async Task<SalesAssistantReply> AskAsync(string channel, string externalId, string message, CancellationToken ct)
    {
        message = (message ?? "").Trim();
        if (message.Length == 0) throw new BusinessRuleException("Say something first.");
        if (message.Length > Opt.MaxMessageChars) message = message[..Opt.MaxMessageChars];
        if (!model.IsConfigured) throw new BusinessRuleException("The sales assistant isn't switched on yet.");

        var conversation = await conversations.GetOrCreateAsync(channel, externalId, ct);
        var usedToday = await conversations.MessagesLast24hAsync(conversation.Id, ct);
        if (usedToday >= Opt.DailyMessagesPerConversation)
            return new SalesAssistantReply("You've reached today's message limit for this chat. Please try again tomorrow, or ask an admin directly.", null, null, true);

        await conversations.AppendAsync(conversation.Id, ChatRoles.User, message, ct);
        var history = await conversations.HistoryAsync(conversation.Id, Opt.HistoryTurns, ct);

        var msgs = new List<AiChatMessage> { new("system", SalesAssistantPrompts.Persona(company.LegalName.Length > 0 ? company.LegalName : company.Key, clock.BusinessToday)) };
        foreach (var t in history) msgs.Add(new(t.Role, t.Text));

        var info = new OrderInfo();
        string? answer = null;
        for (var round = 0; round < Opt.MaxToolRounds && answer is null; round++)
        {
            var reply = await model.CompleteAsync(msgs, SalesAssistantToolbox.Specs, Opt.MaxAnswerTokens, ct);
            if (reply.ToolCalls.Count == 0) { answer = reply.Content; break; }
            msgs.Add(new("assistant", reply.Content, reply.ToolCalls));
            foreach (var call in reply.ToolCalls)
            {
                var result = await tools.ExecuteAsync(conversation.Id, call.Name, call.ArgumentsJson, ct);
                ExtractOrderInfo(result, info);
                msgs.Add(new("tool", result, null, call.Id));
            }
        }
        answer ??= "Sorry, I couldn't finish that. Could you try again, maybe with fewer things at once?";
        answer = answer.Trim();
        await conversations.AppendAsync(conversation.Id, ChatRoles.Assistant, answer, ct);
        // Once a link exists there's nothing left to choose.
        var options = info.CheckoutUrl is null ? info.PaymentOptions : null;
        return new SalesAssistantReply(answer, info.CheckoutUrl, info.QuotationNumber, false, options, info.Total);
    }

    /// <summary>
    /// The customer tapped a payment button. Handled without the model — the link comes straight from the processor — but both sides are
    /// written into the conversation so the assistant knows what happened on the next message.
    /// </summary>
    public async Task<SalesAssistantReply> ChoosePaymentAsync(string channel, string externalId, string provider, CancellationToken ct)
    {
        var conversation = await conversations.GetOrCreateAsync(channel, externalId, ct);
        var label = PaymentProviders.DisplayName(provider);
        await conversations.AppendAsync(conversation.Id, ChatRoles.User, $"[Chose to pay with {label}]", ct);

        string text; string? url = null, number = null;
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(await tools.CheckoutLinkForAsync(conversation.Id, provider, ct))))
        {
            var r = doc.RootElement;
            if (r.TryGetProperty("checkoutUrl", out var u) && u.ValueKind == JsonValueKind.String)
            {
                url = u.GetString();
                number = r.TryGetProperty("quotationNumber", out var q) ? q.GetString() : null;
                var amount = r.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var d) ? d : 0m;
                text = $"Pay ₦{amount:N2} for order {number} with {label} here:\n{url}\n\nYour order is confirmed and sent out as soon as the payment comes through. Reply \"paid\" once you've paid.";
            }
            else text = r.TryGetProperty("error", out var e) ? e.GetString() ?? "Sorry, that didn't work." : "Sorry, that didn't work.";
        }
        await conversations.AppendAsync(conversation.Id, ChatRoles.Assistant, text, ct);
        return new SalesAssistantReply(text, url, number, false);
    }

    private sealed class OrderInfo
    {
        public string? CheckoutUrl, QuotationNumber;
        public IReadOnlyList<string>? PaymentOptions;
        public decimal? Total;
    }

    private static void ExtractOrderInfo(string toolResultJson, OrderInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("checkoutUrl", out var u) && u.ValueKind == JsonValueKind.String) info.CheckoutUrl = u.GetString();
            var numberField = root.TryGetProperty("quotationNumber", out var q1) ? q1 : root.TryGetProperty("number", out var q2) ? q2 : default;
            if (numberField.ValueKind == JsonValueKind.String) info.QuotationNumber = numberField.GetString();
            if (root.TryGetProperty("total", out var t) && t.TryGetDecimal(out var total)) info.Total = total;
            if (root.TryGetProperty("paymentOptions", out var p) && p.ValueKind == JsonValueKind.Array)
                info.PaymentOptions = p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
        }
        catch (JsonException) { /* not every tool result is an object with these fields */ }
    }
}
