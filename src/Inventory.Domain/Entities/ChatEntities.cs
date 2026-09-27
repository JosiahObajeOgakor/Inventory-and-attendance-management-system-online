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

public static class InboundStatuses
{
    public const string Pending = "Pending";
    public const string Done = "Done";
    public const string Failed = "Failed";
}

/// <summary>
/// A customer message waiting to be answered (the WhatsApp inbox). The webhook only stores it and acknowledges Meta at once;
/// a background worker answers it. Meta's message id is unique, so a redelivered webhook is ignored. The generated reply is kept
/// before it is sent, so a retry after a failed send re-sends the same answer instead of asking the model again.
/// </summary>
public class InboundMessage
{
    public long Id { get; set; }
    public string Channel { get; set; } = ChatChannels.WhatsApp;
    /// <summary>Who wrote it (E.164 phone number for WhatsApp).</summary>
    public string Sender { get; set; } = "";
    /// <summary>The channel's own message id (WhatsApp "wamid").</summary>
    public string ExternalId { get; set; } = "";
    public string? Text { get; set; }
    /// <summary>The id of a tapped button (e.g. "pay:alatpay"), when the message is a button tap.</summary>
    public string? ButtonId { get; set; }
    public string Status { get; set; } = InboundStatuses.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    /// <summary>Set while a worker is answering it; if the worker dies, the message becomes claimable again once this passes.</summary>
    public DateTime? LockedUntil { get; set; }
    /// <summary>The generated reply (JSON), kept so a failed send is retried without generating a second answer.</summary>
    public string? ReplyJson { get; set; }
    public string? LastError { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
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
