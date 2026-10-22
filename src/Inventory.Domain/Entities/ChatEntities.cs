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

    // ---- deterministic chat flow (WhatsApp via Twilio) ----
    /// <summary>Where the buying conversation is: see <see cref="ChatStates"/>.</summary>
    public string State { get; set; } = ChatStates.Idle;
    /// <summary>Language the customer is writing in: see <see cref="ChatLanguages"/>. Sticky until the customer clearly switches.</summary>
    public string Language { get; set; } = ChatLanguages.English;
    /// <summary>The order being built (lines, pending question), as JSON. Only ever product ids and quantities — never a price.</summary>
    public string? DraftJson { get; set; }
    /// <summary>AI calls spent on the sale in progress. Hard-capped; reset to 0 when the sale completes or is cancelled.</summary>
    public int AiCalls { get; set; }
}

public static class ChatStates
{
    public const string Idle = "Idle";
    /// <summary>We asked what they want / which product / how many.</summary>
    public const string Collecting = "Collecting";
    public const string AwaitingName = "AwaitingName";
    /// <summary>Web chat only: asked for the customer's WhatsApp number (WhatsApp already knows it).</summary>
    public const string AwaitingPhone = "AwaitingPhone";
    /// <summary>First order only: asked whether we may send reminders and offers. A yes/no in words, like everything else.</summary>
    public const string AwaitingConsent = "AwaitingConsent";
    public const string AwaitingConfirm = "AwaitingConfirm";
    /// <summary>A virtual account was issued; waiting for the transfer.</summary>
    public const string AwaitingPayment = "AwaitingPayment";
}

public static class ChatLanguages
{
    public const string English = "en";
    public const string Pidgin = "pcm";
    public const string Igbo = "ig";
    public const string Hausa = "ha";
    public const string Yoruba = "yo";
    public const string Igala = "igl";
    public static readonly string[] All = [English, Pidgin, Igbo, Hausa, Yoruba, Igala];
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
