using Inventory.Application.Abstractions;
using Inventory.Application.Ai;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant;

/// <summary>Conversation state for the sales assistant. OpenAI calls are stateless, so this is where
/// history and the in-progress quotation live between messages, keyed by (channel, externalId) — a
/// phone number for WhatsApp, a client-generated session id for web chat.</summary>
public sealed class ChatConversationService(IBusinessDbContext db, IClock clock)
{
    public async Task<ChatConversation> GetOrCreateAsync(string channel, string externalId, CancellationToken ct)
    {
        var existing = await db.ChatConversations.FirstOrDefaultAsync(c => c.Channel == channel && c.ExternalId == externalId, ct);
        if (existing is not null) return existing;

        var conversation = new ChatConversation { Channel = channel, ExternalId = externalId, CreatedAt = clock.UtcNow, LastMessageAt = clock.UtcNow };
        db.ChatConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task<IReadOnlyList<ChatTurn>> HistoryAsync(int conversationId, int lastN, CancellationToken ct) =>
        await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Id).Take(lastN).OrderBy(m => m.Id)
            .Select(m => new ChatTurn(m.Role, m.Text)).ToListAsync(ct);

    public async Task AppendAsync(int conversationId, string role, string text, CancellationToken ct)
    {
        db.ChatMessages.Add(new ChatLogMessage { ConversationId = conversationId, Role = role, Text = text, CreatedAt = clock.UtcNow });
        await db.ChatConversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastMessageAt, clock.UtcNow), ct);
        await db.SaveChangesAsync(ct);
    }

    public Task<int> MessagesLast24hAsync(int conversationId, CancellationToken ct)
    {
        var since = clock.UtcNow.AddHours(-24);
        return db.ChatMessages.AsNoTracking().CountAsync(m => m.ConversationId == conversationId && m.Role == ChatRoles.User && m.CreatedAt > since, ct);
    }

    public Task SetQuotationAsync(int conversationId, int quotationId, CancellationToken ct) =>
        db.ChatConversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(u => u.SetProperty(c => c.QuotationId, quotationId), ct);

    public Task LinkCustomerAsync(int conversationId, int customerId, CancellationToken ct) =>
        db.ChatConversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(u => u.SetProperty(c => c.CustomerId, customerId), ct);
}
