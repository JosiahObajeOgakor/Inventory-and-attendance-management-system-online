using System.Text.Json;
using System.Text.RegularExpressions;
using Inventory.Application.Ai;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Application.Campaigns;

/// <summary>
/// Drafts the wording of campaign messages. Per run and per (situation, language) it asks the AI ONCE for a few fresh variants written with placeholders
/// ({name}, {product}, {days}…), checks them hard, and fills the placeholders for each customer from the database. So messages differ from day to day and
/// from person to person, but every name, number, price and date comes from the records — the AI never sees a customer and cannot state a price or a stock
/// level of its own. Anything that fails the checks, or any call beyond the per-run budget, falls back to <see cref="CampaignText"/>.
/// </summary>
public sealed class CampaignWriter(IChatModel model, IOptions<CampaignOptions> options, ILogger<CampaignWriter> log)
{
    public static readonly string[] Placeholders = ["name", "product", "qty", "days", "date", "stock", "price", "total", "runout", "last", "company"];
    private static readonly Regex Token = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private readonly Dictionary<(string Kind, string Lang), string[]> _cache = [];

    public int AiCalls { get; private set; }
    public bool AiAllowed { get; set; } = true;

    private static string LanguageName(string lang) => lang switch
    {
        ChatLanguages.Pidgin or ChatLanguages.Igala => "Nigerian Pidgin",
        ChatLanguages.Igbo => "Igbo",
        ChatLanguages.Hausa => "Hausa",
        _ => "English",
    };

    /// <summary>Variants for this situation in this language, drafted at most once per run.</summary>
    public async Task<string[]> VariantsAsync(string kind, string lang, CancellationToken ct)
    {
        var key = (kind, lang == ChatLanguages.Igala ? ChatLanguages.Pidgin : lang);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var fallback = CampaignText.Variants(kind, key.Item2);
        var chosen = fallback;
        if (AiAllowed && model.IsConfigured && AiCalls < options.Value.MaxAiCallsPerRun)
        {
            AiCalls++;
            var drafted = await DraftAsync(kind, key.Item2, ct);
            // Mix: the fresh AI variants first, the vetted ones after, so a day with one good AI variant still has choice and a bad draft costs nothing.
            if (drafted.Length > 0) chosen = [.. drafted, .. fallback];
        }
        return _cache[key] = chosen;
    }

    private async Task<string[]> DraftAsync(string kind, string lang, CancellationToken ct)
    {
        var system = $$"""
            You write short WhatsApp messages for a Nigerian pet-feed shop's customers. Language: {{LanguageName(lang)}}. Warm, human, specific, never pushy.
            Situation: {{CampaignText.Situation(kind)}}
            Write 3 clearly different messages, each under 45 words. Use these placeholders exactly where natural; they are filled in later from the shop's records:
            {name} (must appear), {product}, {qty}, {days} (days since purchase), {date}, {stock}, {price}, {total}, {runout}, {last} (the customer's previous product), {company}.
            Rules: write NO digits, currency symbols, links or hashtags of your own; do not promise discounts, delivery or anything not stated; do not invent products. Vary the opening and the angle.
            Reply with ONLY JSON: {"variants":["...","...","..."]}
            """;
        try
        {
            var reply = await model.CompleteAsync([new("system", system), new("user", $"Draft the variants for {kind} ({DateTime.UtcNow:yyyy-MM-dd}).")], [], 400, ct);
            return ParseVariants(reply.Content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Campaign AI draft failed; using the built-in wording");
            return [];
        }
    }

    /// <summary>Keeps only variants that are safe to send. Exposed for tests.</summary>
    public static string[] ParseVariants(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var s = content.IndexOf('{'); var e = content.LastIndexOf('}');
        if (s < 0 || e <= s) return [];
        try
        {
            using var doc = JsonDocument.Parse(content[s..(e + 1)]);
            if (!doc.RootElement.TryGetProperty("variants", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(IsSafe).Take(4).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return []; }
    }

    public static bool IsSafe(string v)
    {
        if (v.Length is < 30 or > 380 || v.Contains('\n') || v.Contains('\r')) return false;
        if (!v.Contains("{name}")) return false;
        if (v.Any(char.IsDigit) || v.Contains('₦') || v.Contains("http", StringComparison.OrdinalIgnoreCase) || v.Contains('#') || v.Contains("www.", StringComparison.OrdinalIgnoreCase)) return false;
        return Token.Matches(v).All(m => Placeholders.Contains(m.Groups[1].Value));
    }

    /// <summary>Picks a variant that can be fully filled from these facts, rotating by customer and day so neighbours (and tomorrow) read differently.</summary>
    public static string Render(string[] variants, IReadOnlyDictionary<string, string> facts, int seed)
    {
        var usable = variants.Where(v => Token.Matches(v).All(m => facts.ContainsKey(m.Groups[1].Value))).ToArray();
        var pool = usable.Length > 0 ? usable : CampaignText.Variants(CampaignText.Reorder, ChatLanguages.English);
        var pick = pool[Math.Abs(seed) % pool.Length];
        return Token.Replace(pick, m => facts.TryGetValue(m.Groups[1].Value, out var val) ? val : m.Value);
    }
}
