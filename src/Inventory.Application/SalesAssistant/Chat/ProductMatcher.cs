namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>One sellable product as the chat sees it: real retail price and real, reservation-adjusted stock from the database.</summary>
public sealed record CatalogItem(int Id, string Name, string Unit, decimal Price, int Available)
{
    public string[] Tokens { get; } = ChatLexicon.Tokens(ChatLexicon.Normalize(Name));
}

/// <summary>One thing the customer asked for. <see cref="ProductId"/> is pre-resolved when the AI helped; otherwise it is matched from <see cref="Text"/>.</summary>
public sealed record RawLine(string Text, int? Qty, int? ProductId = null);

public sealed record ParsedLine(RawLine Raw, IReadOnlyList<CatalogItem> Matches, IReadOnlyList<CatalogItem> Near);

/// <summary>
/// Turns "abeg i wan buy 2 bags of royal canin and 3 chicken feed" into lines of (product, quantity) by comparing words with real product names —
/// no AI. Tolerates plurals, prefixes and a typo or two. Anything it can't pin to exactly one product it reports as ambiguous or unknown,
/// and the flow asks the customer by name (never "reply 1 for…").
/// </summary>
public static class ProductMatcher
{
    /// <summary>Splits a message into the separate things asked for, each with an optional quantity.</summary>
    public static List<RawLine> Split(string normalized)
    {
        var tokens = ChatLexicon.Tokens(normalized);
        var segments = new List<List<string>> { new() };
        foreach (var t in tokens)
        {
            if (ChatLexicon.ItemSeparators.Contains(t)) { if (segments[^1].Count > 0) segments.Add([]); continue; }
            segments[^1].Add(t);
        }
        var lines = new List<RawLine>();
        foreach (var seg in segments)
        {
            int? qty = null; var qtyAfterWords = false; var words = new List<string>();
            void Flush()
            {
                if (words.Count > 0) lines.Add(new RawLine(string.Join(' ', words), qty));
                qty = null; qtyAfterWords = false; words = [];
            }
            foreach (var t in seg)
            {
                if (TryQuantity(t, out var q))
                {
                    if (qty is not null) Flush();            // "2 rice 3 beans": a second quantity starts a new item
                    qty = q; qtyAfterWords = words.Count > 0;
                    continue;
                }
                if (ChatLexicon.Filler.Contains(t)) continue;
                if (qty is not null && qtyAfterWords) Flush();   // "rice 2 beans 3": a word after "name qty" starts a new item
                words.Add(t);
            }
            Flush();
        }
        return lines;
    }

    /// <summary>The first number in the text ("3", "x3", "three", "uku"), or null. <paramref name="ignoreWordOne"/> stops "the 5kg one" being read as a quantity of 1.</summary>
    public static int? FirstQuantity(string normalized, bool ignoreWordOne = false)
    {
        foreach (var t in ChatLexicon.Tokens(normalized))
        {
            if (ignoreWordOne && t is "one" or "otu" or "daya") continue;
            if (TryQuantity(t, out var q)) return q;
        }
        return null;
    }

    private static bool TryQuantity(string token, out int qty)
    {
        qty = 0;
        var t = token;
        if (t.Length > 1 && t[0] == 'x' && t[1..].All(char.IsDigit)) t = t[1..];
        else if (t.Length > 1 && t[^1] == 'x' && t[..^1].All(char.IsDigit)) t = t[..^1];
        if (t.All(char.IsDigit) && int.TryParse(t, out qty) && qty is > 0 and <= 9999) return true;
        return ChatLexicon.NumberWords.TryGetValue(token, out qty);
    }

    public static ParsedLine Match(RawLine raw, IReadOnlyList<CatalogItem> catalogue)
    {
        if (raw.ProductId is int id)
        {
            var hit = catalogue.FirstOrDefault(c => c.Id == id);
            return new ParsedLine(raw, hit is null ? [] : [hit], []);
        }
        var q = ChatLexicon.Tokens(raw.Text);
        if (q.Length == 0) return new ParsedLine(raw, [], []);

        var scored = catalogue.Select(item =>
        {
            var matchedQ = q.Count(qt => item.Tokens.Any(pt => TokenMatch(qt, pt)));
            var matchedP = item.Tokens.Count(pt => q.Any(qt => TokenMatch(qt, pt)));
            return (item, matchedQ, matchedP);
        }).ToList();

        var full = scored.Where(s => s.matchedQ == q.Length).ToList();
        if (full.Count > 1)
        {
            // Prefer a product whose whole name was said ("rice 25kg" → "Rice 25kg") over ones that merely contain the word.
            var exact = full.Where(s => s.matchedP == s.item.Tokens.Length).ToList();
            if (exact.Count == 1) full = exact;
        }
        var near = scored.Where(s => s.matchedQ > 0 && s.matchedQ < q.Length && s.matchedQ * 2 >= q.Length)
            .OrderByDescending(s => s.matchedQ).Take(4).Select(s => s.item).ToList();
        return new ParsedLine(raw, full.Select(s => s.item).ToList(), near);
    }

    /// <summary>Same word, plural, prefix of a longer word (≥4 letters), or one typo (two for long words).</summary>
    public static bool TokenMatch(string a, string b)
    {
        if (a == b) return true;
        a = Singular(a); b = Singular(b);
        if (a == b) return true;
        if (a.Length >= 4 && b.StartsWith(a, StringComparison.Ordinal)) return true;
        if (b.Length >= 4 && a.StartsWith(b, StringComparison.Ordinal)) return true;
        if (a.Length < 5 || b.Length < 5 || a.Any(char.IsDigit) || b.Any(char.IsDigit)) return false;
        return Levenshtein(a, b, a.Length >= 8 ? 2 : 1) <= (a.Length >= 8 ? 2 : 1);
    }

    private static string Singular(string s) => s.Length > 3 && s.EndsWith('s') && !s.EndsWith("ss") ? s[..^1] : s;

    private static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1]; cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }
}
