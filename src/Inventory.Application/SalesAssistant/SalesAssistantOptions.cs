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
}
