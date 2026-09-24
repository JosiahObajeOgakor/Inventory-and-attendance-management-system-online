using System.Text.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Options;
using AiChatMessage = Inventory.Application.Ai.ChatMessage;

namespace Inventory.Application.SalesAssistant;

public sealed record SalesAssistantReply(string Text, string? CheckoutUrl, string? QuotationNumber, bool Limited);

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

        string? checkoutUrl = null, quotationNumber = null;
        string? answer = null;
        for (var round = 0; round < Opt.MaxToolRounds && answer is null; round++)
        {
            var reply = await model.CompleteAsync(msgs, SalesAssistantToolbox.Specs, Opt.MaxAnswerTokens, ct);
            if (reply.ToolCalls.Count == 0) { answer = reply.Content; break; }
            msgs.Add(new("assistant", reply.Content, reply.ToolCalls));
            foreach (var call in reply.ToolCalls)
            {
                var result = await tools.ExecuteAsync(conversation.Id, call.Name, call.ArgumentsJson, ct);
                (checkoutUrl, quotationNumber) = ExtractOrderInfo(result, checkoutUrl, quotationNumber);
                msgs.Add(new("tool", result, null, call.Id));
            }
        }
        answer ??= "Sorry, I couldn't finish that. Could you try again, maybe with fewer things at once?";
        answer = answer.Trim();
        await conversations.AppendAsync(conversation.Id, ChatRoles.Assistant, answer, ct);
        return new SalesAssistantReply(answer, checkoutUrl, quotationNumber, false);
    }

    private static (string? Checkout, string? Quotation) ExtractOrderInfo(string toolResultJson, string? checkout, string? quotation)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("checkoutUrl", out var u) && u.ValueKind == JsonValueKind.String) checkout = u.GetString();
            var numberField = root.TryGetProperty("quotationNumber", out var q1) ? q1 : root.TryGetProperty("number", out var q2) ? q2 : default;
            if (numberField.ValueKind == JsonValueKind.String) quotation = numberField.GetString();
        }
        catch (JsonException) { /* not every tool result is an object with these fields */ }
        return (checkout, quotation);
    }
}
