namespace Inventory.Api.Infrastructure;

/// <summary>
/// Which sales assistant answers on the website chat: "flow" (default: the same conversation as the WhatsApp line: it answers questions in any state,
/// sells from every store, AlatPay account in the chat, quotations, five languages, AI only when the words can't be understood) or "ai" (the older
/// AI assistant). The Twilio WhatsApp line always uses "flow". Set SalesAssistant:Engine=ai in the server's settings to switch back.
/// </summary>
public static class SalesAssistantEngine
{
    public static bool UsesFlow(IConfiguration config) => !string.Equals(config["SalesAssistant:Engine"], "ai", StringComparison.OrdinalIgnoreCase);
}