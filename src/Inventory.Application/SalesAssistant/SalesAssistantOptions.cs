namespace Inventory.Application.SalesAssistant;

/// <summary>Tunables for the customer-facing sales assistant (WhatsApp + web chat). Uses the same OpenAI key/model as the admin assistant (AiOptions).</summary>
public sealed class SalesAssistantOptions
{
    public const string Section = "SalesAssistant";
    public int MaxToolRounds { get; set; } = 6;
    public int MaxAnswerTokens { get; set; } = 500;
    public int VetToolMaxTokens { get; set; } = 350;
    public int HistoryTurns { get; set; } = 16;
    public int MaxMessageChars { get; set; } = 1000;
    public int DailyMessagesPerConversation { get; set; } = 60;
    /// <summary>Hard ceiling on AI calls for ONE sale (counted per conversation, reset when the sale completes or is cancelled). A normal sale uses none.</summary>
    public int MaxAiCallsPerSale { get; set; } = 10;
    /// <summary>New quotations one number (or one website visitor) can get in a rolling 24 hours. Asking again shows today's quotation instead.</summary>
    public int QuotesPerDay { get; set; } = 1;
    /// <summary>How long stock is held for a customer who has been given a payment account but hasn't paid.</summary>
    public int ReservationMinutes { get; set; } = 60;
    /// <summary>Optional JSON file with native-speaker wording and recognition words (see ChatText). Igala lives here.</summary>
    public string PhrasesFile { get; set; } = "";
}
