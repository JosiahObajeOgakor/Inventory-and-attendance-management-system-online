namespace Inventory.Application.Messaging;

/// <summary>Sends a text message to a customer. Implemented over Termii in Infrastructure; not configured until <c>Notify:SmsApiKey</c> is set.</summary>
public interface ISmsSender
{
    bool IsConfigured { get; }
    /// <param name="toDigits">Customer number, digits only with country code (2348012345678).</param>
    Task<bool> TrySendAsync(string toDigits, string text, CancellationToken ct);
}
