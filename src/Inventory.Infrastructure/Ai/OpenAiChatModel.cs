using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventory.Application.Ai;
using Inventory.Application.Common;
using Inventory.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Ai;

/// <summary>OpenAI chat-completions over plain HTTPS. The key is read from configuration (environment) and is never logged or echoed in an error.</summary>
public sealed class OpenAiChatModel(HttpClient http, IOptions<AiOptions> options) : IChatModel
{
    private AiOptions Opt => options.Value;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Opt.ApiKey);

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessRuleException("The assistant isn't switched on yet: no OpenAI key is configured on the server.");

        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role };
            if (m.Content is not null || m.ToolCalls is null) o["content"] = m.Content ?? "";
            if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
            if (m.ToolCalls is { Count: > 0 })
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                }).ToArray());
            msgs.Add(o);
        }

        var body = new JsonObject { ["model"] = Opt.Model, ["messages"] = msgs, ["max_completion_tokens"] = maxTokens };
        // Reasoning models reject a temperature; the small chat models take a low one so answers stick to the data.
        if (!Opt.Model.StartsWith("o", StringComparison.OrdinalIgnoreCase) && !Opt.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)) body["temperature"] = 0.2;
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.ParametersJsonSchema) },
            }).ToArray());
            body["tool_choice"] = "auto";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Opt.ApiKey);

        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BusinessRuleException("The assistant took too long to answer. Please try again."); }
        catch (HttpRequestException) { throw new BusinessRuleException("The assistant can't reach OpenAI right now. Check the server's internet connection."); }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new BusinessRuleException(res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "OpenAI rejected the API key. Check OpenAI__ApiKey on the server.",
                HttpStatusCode.TooManyRequests => "OpenAI says the account is out of quota or busy. Check billing, then try again.",
                HttpStatusCode.NotFound => $"OpenAI doesn't know the model “{Opt.Model}”. Set OpenAI__Model to one your account can use.",
                _ => "OpenAI couldn't answer that just now. Please try again.",
            });

            var root = JsonNode.Parse(text)!;
            var msg = root["choices"]?[0]?["message"];
            var calls = new List<ToolCall>();
            if (msg?["tool_calls"] is JsonArray arr)
                foreach (var c in arr)
                    calls.Add(new ToolCall(c?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), c?["function"]?["name"]?.GetValue<string>() ?? "", c?["function"]?["arguments"]?.GetValue<string>() ?? "{}"));
            return new ChatReply(msg?["content"]?.GetValue<string>(), calls, root["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, root["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0);
        }
    }
}

public class AiUsageEntry
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public DateTime AtUtc { get; set; }
}

public sealed class AiUsageStore(IdentityStore store) : IAiUsageStore
{
    public async Task<IReadOnlyList<DateTime>> RecentAsync(int userId, TimeSpan window, CancellationToken ct)
    {
        var since = DateTime.UtcNow - window;
        return await store.Set<AiUsageEntry>().AsNoTracking().Where(e => e.UserId == userId && e.AtUtc > since).OrderBy(e => e.AtUtc).Select(e => e.AtUtc).ToListAsync(ct);
    }

    public async Task<long> ReserveAsync(int userId, CancellationToken ct)
    {
        var e = new AiUsageEntry { UserId = userId, AtUtc = DateTime.UtcNow };
        store.Add(e);
        await store.SaveChangesAsync(ct);
        return e.Id;
    }

    public async Task ReleaseAsync(long id, CancellationToken ct) =>
        await store.Set<AiUsageEntry>().Where(e => e.Id == id).ExecuteDeleteAsync(ct);
}
