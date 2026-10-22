namespace Inventory.Api.Infrastructure;

/// <summary>
/// Which sales assistant answers customers on the Meta WhatsApp number and the website chat: "ai" (default: the AI assistant those channels have always used)
/// or "flow" (the word-based conversation: no AI unless the words can't be understood, quotations, delivery questions, five languages). The Twilio number always
/// uses "flow". Set SalesAssistant:Engine in the server's settings to switch; nothing else changes.
/// </summary>
public static class SalesAssistantEngine
{
    public static bool UsesFlow(IConfiguration config) => string.Equals(config["SalesAssistant:Engine"], "flow", StringComparison.OrdinalIgnoreCase);
}
