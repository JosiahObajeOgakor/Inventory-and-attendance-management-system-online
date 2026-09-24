namespace Inventory.Domain.Entities;

public static class ChatChannels
{
    public const string WhatsApp = "whatsapp";
    public const string WebChat = "webchat";
}

/// <summary>One row per phone number (WhatsApp) or browser session (web chat) talking to the sales assistant.
/// OpenAI calls are stateless, so this is where conversation history and the in-progress order live between messages.</summary>
public class ChatConversation
{
    public int Id { get; set; }
    public string Channel { get; set; } = ChatChannels.WebChat;
    /// <summary>E.164 phone number for WhatsApp, or an opaque client-generated session id for web chat.</summary>
    public string ExternalId { get; set; } = "";
    public int? CustomerId { get; set; }
    /// <summary>The quotation currently being built/paid for in this conversation, if any.</summary>
    public int? QuotationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
}

public static class ChatRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>Named ChatLogMessage, not ChatMessage, to avoid colliding with Inventory.Application.Ai.ChatMessage (the OpenAI wire-format record) in any file that needs both.</summary>
public class ChatLogMessage
{
    public long Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = ChatRoles.User;
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
