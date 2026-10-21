using System.Globalization;
using System.Text;
using Inventory.Domain.Entities;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// Everything the chat understands WITHOUT calling the AI: normalising text (tone marks, dot-below vowels, Hausa hooked letters),
/// spotting the language, yes / no / cancel / "I have paid" / "show me products", and number words.
/// Each list is plain data on purpose so a native speaker can read and correct it (see <see cref="ChatText"/> for the override file).
/// </summary>
public static class ChatLexicon
{
    /// <summary>Lower-case, strips tone marks and dot-below (ọ→o, ị→i, ụ→u), maps Hausa ɗ ƙ ɓ, drops apostrophes, everything else non-alphanumeric becomes a space.</summary>
    public static string Normalize(string? s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var raw in d)
        {
            var ch = char.ToLowerInvariant(raw);
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is '\'' or '’' or '‘' or '`') continue;
            ch = ch switch { 'ɗ' => 'd', 'ƙ' => 'k', 'ɓ' => 'b', 'ŋ' => 'n', _ => ch };
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string[] Tokens(string normalized) => normalized.Length == 0 ? [] : normalized.Split(' ');

    /// <summary>True when any phrase appears in the text as whole words.</summary>
    public static bool Has(string normalized, params string[] phrases)
    {
        var padded = " " + normalized + " ";
        foreach (var p in phrases) if (padded.Contains(" " + p + " ", StringComparison.Ordinal)) return true;
        return false;
    }

    // ---------------------------------------------------------------- language

    /// <summary>Words that give a language away. English is the default and has no list. Igala is empty until a native speaker fills it in (override file).</summary>
    public static readonly Dictionary<string, string[]> LanguageMarkers = new()
    {
        [ChatLanguages.Pidgin] = ["abeg", "wetin", "dey", "oga", "madam", "una", "wahala", "sabi", "pikin", "wey", "dem", "comot", "chop", "sef", "abi", "oya", "jare", "shey", "how far", "no be", "i don", "e don", "make i", "make we", "sharp sharp", "i wan", "we wan", "una get", "you get"],
        [ChatLanguages.Igbo] = ["biko", "nna", "nne", "ndewo", "kedu", "daalu", "imeela", "achoro", "azuta", "zuta", "ugwo", "ihe", "ole", "nnoo", "mba", "ahia", "anyi", "unu", "enwere", "maka", "gwa m", "onu ahia", "ego", "ee"],
        [ChatLanguages.Hausa] = ["sannu", "nagode", "ina", "kwana", "yaya", "nawa", "kudi", "saya", "siya", "zan", "kuma", "abinci", "farashi", "akwai", "babu", "don allah", "barka", "yawa", "wallahi", "toh", "ina son", "ina so", "na biya", "na tura", "kaya", "fatan"],
        [ChatLanguages.Igala] = [],
    };

    /// <summary>Picks the language to answer in. Sticky: a single stray word doesn't flip a conversation, two clues do (or one clue when we've only assumed English so far).</summary>
    public static string DetectLanguage(string normalized, string current)
    {
        string best = current; var bestScore = 0;
        foreach (var (lang, words) in LanguageMarkers)
        {
            var score = words.Count(w => Has(normalized, w));
            if (score > bestScore) { best = lang; bestScore = score; }
        }
        if (bestScore == 0 || best == current) return current;
        return bestScore >= 2 || current == ChatLanguages.English ? best : current;
    }

    // ---------------------------------------------------------------- intents

    private static readonly string[] YesWords = ["yes", "yeah", "yep", "yup", "ok", "okay", "sure", "confirm", "confirmed", "correct", "proceed", "continue", "oya", "sharp", "ehen", "eh", "ee", "eeh", "toh", "to", "tabbas", "yarda", "kwenye", "mee", "gaa"];
    private static readonly string[] YesPhrases = ["do am", "go ahead", "make we go", "no wahala", "no problem", "o di mma", "mee ya", "ya yi", "ci gaba", "na yarda", "gaa n ihu", "e", "generate it", "send am", "give me the account", "send account"];
    private static readonly string[] NoWords = ["no", "nope", "nah", "mba", "aa", "cancel", "stop", "reset", "soke", "kagbuo", "restart", "wrong", "change"];
    private static readonly string[] CancelWords = ["cancel", "stop", "reset", "forget it", "forget am", "leave am", "leave it", "start over", "kagbuo", "hapu ya", "soke", "bar shi", "i no want again", "i dont want again", "i do not want again", "no want again", "abeg stop"];
    private static readonly string[] PaidWords = ["paid", "i paid", "i have paid", "i dont pay", "i don pay", "i don send", "i have sent", "sent it", "payment done", "transfer done", "transfer don enter", "done", "its done", "money sent", "i sent", "akwuola m ugwo", "akwuola ugwo", "ezigala m", "emela m ego", "na biya", "na tura", "an biya", "na gama", "status", "any update", "confirm payment", "check payment", "did you get it", "you don see am", "you see am", "e don enter"];
    private static readonly string[] ProductWords = ["products", "product", "catalogue", "catalog", "menu", "list", "prices", "price list", "what do you sell", "what do you have", "what you get", "wetin you get", "wetin una get", "wetin una sell", "wetin you sell", "wetin dey", "available", "ihe unu na ere", "ihe unu nwere", "kedu ihe unu nwere", "onu ahia", "ahia", "farashi", "menene kuke sayarwa", "me kuke da shi", "kaya", "show me"];
    private static readonly string[] PriceWords = ["price", "cost", "how much", "much", "nawa", "ole ego", "ego ole", "onu ahia", "farashi", "nawa ne", "how much be"];
    private static readonly string[] GreetingWords = ["hi", "hello", "hey", "hiya", "good morning", "good afternoon", "good evening", "good day", "how far", "how you dey", "sup", "yo", "nnoo", "ndewo", "kedu", "sannu", "barka", "ina kwana", "ina wuni", "salam", "assalamu alaikum", "start", "begin", "start order", "order"];

    public static bool IsYes(string n)
    {
        var t = Tokens(n);
        if (t.Length == 0 || t.Length > 6) return false;
        if (Has(n, "no wahala", "no problem")) return true;
        if (t.Any(w => NoWords.Contains(w)) && !Has(n, "do am", "go ahead")) return false;
        return t.Any(w => YesWords.Contains(w)) || Has(n, YesPhrases);
    }

    public static bool IsNo(string n)
    {
        var t = Tokens(n);
        if (t.Length == 0 || t.Length > 6 || Has(n, "no wahala", "no problem")) return false;
        return t.Any(w => NoWords.Contains(w)) || Has(n, "no be so", "a a", "ba haka ba", "i no want", "i dont want", "i do not want");
    }

    private static readonly string[] OptOutPhrases = ["unsubscribe", "opt out", "optout", "stop messages", "stop all", "stop promotions", "stop offers", "stop reminders", "stop sending", "stop texting", "no more messages", "dont message me", "do not message me", "daina aika", "daina aiko", "kwusi izipu", "kwusi ozi", "ka a kwusi"];
    private static readonly string[] OptInPhrases = ["subscribe", "opt in", "optin", "start messages", "start reminders", "send me offers", "send me reminders", "ci gaba da aiko", "gaba da aiko"];
    /// <summary>"STOP" on its own means stop the marketing messages; inside an order it still means cancel the order (see ChatFlowService).</summary>
    public static bool IsOptOut(string n) => Has(n, OptOutPhrases);
    public static bool IsBareStop(string n) => n is "stop" or "stop it" or "please stop" or "abeg stop";
    public static bool IsOptIn(string n) => !IsOptOut(n) && Has(n, OptInPhrases);

    public static bool IsCancel(string n) => Has(n, CancelWords);
    public static bool IsPaidClaim(string n) => Has(n, PaidWords);
    public static bool IsProducts(string n) => Has(n, ProductWords);
    public static bool IsPriceQuestion(string n) => Has(n, PriceWords);
    public static bool IsGreeting(string n) => Tokens(n).Length <= 6 && Has(n, GreetingWords);

    // ---------------------------------------------------------------- numbers & filler

    public static readonly Dictionary<string, int> NumberWords = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["dozen"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["fifty"] = 50,
        // Igbo
        ["otu"] = 1, ["abuo"] = 2, ["ato"] = 3, ["ano"] = 4, ["ise"] = 5, ["isii"] = 6, ["asaa"] = 7, ["asato"] = 8, ["itoolu"] = 9, ["iri"] = 10,
        // Hausa
        ["daya"] = 1, ["biyu"] = 2, ["uku"] = 3, ["hudu"] = 4, ["biyar"] = 5, ["shida"] = 6, ["bakwai"] = 7, ["takwas"] = 8, ["tara"] = 9, ["goma"] = 10,
    };

    /// <summary>Words around a product name that are not part of it. Matching ignores them so "abeg I wan buy 2 bags of rice" reduces to "rice".</summary>
    public static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "i", "me", "my", "we", "you", "u", "want", "wan", "wants", "buy", "get", "give", "need", "like", "please", "pls", "plz", "abeg", "biko", "oga", "madam", "sir", "nna", "nne",
        "the", "a", "an", "of", "for", "some", "bag", "bags", "pack", "packs", "piece", "pieces", "pcs", "pc", "unit", "units", "carton", "cartons", "x", "also", "to", "dey", "go",
        "make", "let", "order", "add", "more", "another", "achoro", "azuta", "zuta", "ina", "son", "so", "zan", "saya", "siya", "ba", "ni", "mini", "is", "are", "can", "could", "would",
        "how", "much", "many", "price", "cost", "prices", "naira", "be", "wetin", "what", "which", "na", "am", "it", "this", "that", "kuma", "da", "and", "with", "plus", "ka", "m", "gi",
        "ya", "mu", "ku", "nke", "ihe", "ole", "nawa", "ne", "hi", "hello", "hey", "good", "morning", "afternoon", "evening", "thanks", "thank", "you", "pay", "take", "put", "send",
        "chose", "choro", "ugwo", "ego", "kudi", "yes", "no", "ok", "okay", "oya", "sharp",
        // greetings and forms of address that often open an order ("Ndewo, achọrọ m 2 rice")
        "ndewo", "nnoo", "kedu", "sannu", "barka", "salam", "assalamu", "alaikum", "imeela", "nagode", "daalu", "kwana", "wuni", "dear", "boss", "bros", "brother", "sister", "aunty", "uncle", "mama", "papa", "day", "sup", "yo", "hiya", "far", "afternoon",
    };

    /// <summary>Separators between two products in one message: "2 rice and 3 beans", "rice, beans", "2 rice na 3 beans", "shinkafa 2 da wake 3".</summary>
    public static readonly string[] ItemSeparators = ["and", "plus", "na", "with", "kuma", "da", "also", "then"];
}
