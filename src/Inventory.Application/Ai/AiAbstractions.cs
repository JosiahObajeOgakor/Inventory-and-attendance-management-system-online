namespace Inventory.Application.Ai;

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ChatMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);
public sealed record ToolSpec(string Name, string Description, string ParametersJsonSchema);
public sealed record ChatReply(string? Content, IReadOnlyList<ToolCall> ToolCalls, int PromptTokens, int CompletionTokens);

/// <summary>The language model behind the assistant. Implemented over OpenAI's chat-completions API in Infrastructure.</summary>
public interface IChatModel
{
    bool IsConfigured { get; }
    Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken ct);
}

/// <summary>Counts assistant questions per person so the daily allowance survives restarts and applies across both businesses.</summary>
public interface IAiUsageStore
{
    /// <summary>Times of this user's questions in the last <paramref name="window"/>, oldest first.</summary>
    Task<IReadOnlyList<DateTime>> RecentAsync(int userId, TimeSpan window, CancellationToken ct);
    Task<long> ReserveAsync(int userId, CancellationToken ct);
    Task ReleaseAsync(long id, CancellationToken ct);
}

public sealed class AiOptions
{
    public const string Section = "OpenAI";
    public string ApiKey { get; set; } = "";
    /// <summary>The cheapest tool-capable model. Change it here without touching code.</summary>
    public string Model { get; set; } = "gpt-4.1-nano";
    public int DailyMessageLimit { get; set; } = 10;
    public int MaxAnswerTokens { get; set; } = 450;
}
