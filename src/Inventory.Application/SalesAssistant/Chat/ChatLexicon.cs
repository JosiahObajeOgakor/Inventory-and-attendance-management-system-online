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
        [ChatLanguages.Pidgin] = ["abeg", "wetin", "dey", "oga", "madam", "una", "wahala", "sabi", "pikin", "wey", "dem", "comot", "chop", "sef", "abi", "oya", "jare", "shey", "how far", "no be", "i don", "e don", "make i", "make we", "sharp sharp", "i wan", "we wan", "una get", "you get", "na"],
        [ChatLanguages.Igbo] = ["biko", "nna", "nne", "ndewo", "kedu", "daalu", "imeela", "achoro", "azuta", "zuta", "ugwo", "ihe", "ole", "nnoo", "mba", "ahia", "anyi", "unu", "enwere", "maka", "gwa m", "onu ahia", "ego", "ee"],
        [ChatLanguages.Hausa] = ["sannu", "nagode", "ina", "kwana", "yaya", "nawa", "kudi", "saya", "siya", "zan", "kuma", "abinci", "farashi", "akwai", "babu", "don allah", "barka", "yawa", "wallahi", "toh", "ina son", "ina so", "na biya", "na tura", "kaya", "fatan"],
        [ChatLanguages.Yoruba] = ["jowo", "mo fe", "mo nfe", "bawo ni", "e kaaro", "e kaasan", "e kaale", "kaaro", "kaasan", "melo", "beeni", "rara", "e se", "mo ti san", "awon oja", "owo re", "e jowo", "ewo ni", "oja yin"],
        [ChatLanguages.Igala] = [],
    };

    private static readonly string[] EnglishWords = ["i want", "i would like", "i need", "what", "which", "please", "the", "your", "my", "is", "are", "do you", "can i", "can you", "how much", "give me", "send me", "have", "thanks", "thank you", "list", "price", "prices"];
    /// <summary>General words for what the shop sells: "dog food" is a request to see the range, not a product name.</summary>
    private static readonly string[] GenericWords = ["dog food", "cat food", "pet food", "puppy food", "kitten food", "food", "feed", "feeds", "dog", "dogs", "cat", "cats", "puppy", "kitten", "pet", "pets"];
    public static bool IsGeneric(string n) => Has(n, GenericWords);

    /// <summary>Picks the language to answer in. Sticky: a single stray word doesn't flip a conversation, two clues do (or one clue when we've only assumed English so far).</summary>
    public static string DetectLanguage(string normalized, string current)
    {
        string best = current; var bestScore = 0;
        foreach (var (lang, words) in LanguageMarkers)
        {
            var score = words.Count(w => Has(normalized, w));
            if (score > bestScore) { best = lang; bestScore = score; }
        }
        // English has no markers list (it is the default), but a message plainly in English ("what are your list?") switches back to it from a language
        // picked up earlier; one English word in a longer message is enough when nothing else is there to say otherwise.
        if (bestScore == 0 && current != ChatLanguages.English && Tokens(normalized).Length >= 2 && EnglishWords.Count(w => Has(normalized, w)) >= 1) return ChatLanguages.English;
        if (bestScore == 0 || best == current) return current;
        return bestScore >= 2 || current == ChatLanguages.English ? best : current;
    }

    // ---------------------------------------------------------------- intents

    private static readonly string[] YesWords = ["yes", "yeah", "yep", "yup", "ok", "okay", "sure", "confirm", "confirmed", "correct", "proceed", "continue", "oya", "sharp", "ehen", "eh", "ee", "eeh", "toh", "to", "tabbas", "yarda", "kwenye", "mee", "gaa", "beeni", "bee", "daju", "odara"];
    private static readonly string[] YesPhrases = ["do am", "go ahead", "make we go", "no wahala", "no problem", "o di mma", "mee ya", "ya yi", "ci gaba", "na yarda", "gaa n ihu", "o da", "o dara", "e", "generate it", "send am", "give me the account", "send account"];
    private static readonly string[] NoWords = ["no", "nope", "nah", "mba", "aa", "cancel", "stop", "reset", "soke", "kagbuo", "restart", "wrong", "change", "rara", "beeko"];
    private static readonly string[] CancelWords = ["cancel", "stop", "reset", "forget it", "forget am", "leave am", "leave it", "start over", "kagbuo", "hapu ya", "soke", "bar shi", "i no want again", "i dont want again", "i do not want again", "no want again", "abeg stop", "fagile", "ma ra mo", "mi o fe mo", "emi ko fe mo"];
    private static readonly string[] PaidWords = ["paid", "i paid", "i have paid", "i dont pay", "i don pay", "i don send", "i have sent", "sent it", "payment done", "transfer done", "transfer don enter", "done", "its done", "money sent", "i sent", "akwuola m ugwo", "akwuola ugwo", "ezigala m", "emela m ego", "na biya", "na tura", "an biya", "na gama", "status", "any update", "confirm payment", "check payment", "did you get it", "you don see am", "you see am", "e don enter", "mo ti san", "mo ti fi owo ranse", "mo san owo", "owo ti wole", "mo ti ranse"];
    private static readonly string[] ProductWords = ["products", "product", "catalogue", "catalog", "menu", "list", "prices", "price list", "what do you sell", "what do you have", "what you get", "wetin you get", "wetin una get", "wetin una sell", "wetin you sell", "wetin dey", "available", "ihe unu na ere", "ihe unu nwere", "kedu ihe unu nwere", "onu ahia", "ahia", "farashi", "menene kuke sayarwa", "me kuke da shi", "kaya", "show me", "awon oja", "kini e ni", "ohun ti e ni", "oja yin", "kini e n ta"];
    private static readonly string[] PriceWords = ["price", "cost", "how much", "much", "nawa", "ole ego", "ego ole", "onu ahia", "farashi", "nawa ne", "how much be", "melo", "owo melo", "elo"];
    private static readonly string[] GreetingWords = ["hi", "hello", "hey", "hiya", "good morning", "good afternoon", "good evening", "good day", "how far", "how you dey", "sup", "yo", "nnoo", "ndewo", "kedu", "sannu", "barka", "ina kwana", "ina wuni", "salam", "assalamu alaikum", "start", "begin", "start order", "order", "e kaaro", "e kaasan", "e kaale", "bawo ni", "pele"];

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
    /// <summary>"I have a question" / "I want to ask something" with no question in it yet: ask them to go ahead (no price, stock or product words).</summary>
    public static bool IsQuestionIntro(string n) => Has(n, ["question", "questions", "enquiry", "inquiry", "want ask", "wan ask", "ask something", "ask you"]) && !Has(n, PriceWords) && !n.Contains("how much");
    public static bool IsPriceQuestion(string n) => Has(n, PriceWords);

    private static readonly string[] QuoteWords = ["quote", "quotes", "quotation", "quotations", "proforma", "pro forma", "price quote", "kwote", "give me quotation", "send me quotation", "request quotation"];
    private static readonly string[] WaybillWords = ["waybill", "waybills", "way bill", "delivery status", "where is my order", "where my order dey", "track my order", "order status", "dispatch", "tracking", "delivery update", "isiro", "ibo"];
    private static readonly string[] SendWords = ["send", "send it", "send am", "send quote", "send quotation", "yes send", "ok send", "tura", "zipu", "ran"];
    private static readonly string[] PayWords = ["pay", "pay now", "pay am", "buy now", "i want to pay", "i wan pay", "biya", "kwuo ugwo", "san"];
    // ---------------------------------------------------------------- enquiries (the desk answers these in any state, then nudges back to the sale)

    public const string AskHuman = "human", AskDelivery = "delivery", AskLocation = "location", AskHours = "hours", AskPayHow = "payhow",
        AskDiscount = "discount", AskRecommend = "recommend", AskInfo = "info", AskStock = "stock", AskPrice = "price";

    private static readonly (string Intent, string[] Words)[] Enquiries =
    [
        (AskHuman, ["human", "real person", "agent", "customer care", "customer service", "call me", "speak to someone", "talk to someone", "speak with someone", "talk to a person", "manager", "your number", "phone number", "make person call me"]),
        (AskPayHow, ["how do i pay", "how to pay", "how can i pay", "payment method", "payment methods", "pay on delivery", "payment on delivery", "cash on delivery", "pos", "can i pay with card", "pay with card", "is it safe", "safe to pay", "legit", "scam", "trust", "how una dey collect money"]),
        (AskDelivery, ["deliver", "delivery", "deliveries", "dispatch", "ship", "shipping", "send it to", "send am come", "bring it", "bring am", "rider", "logistics", "deliver to", "dey deliver", "do una deliver", "una dey deliver", "delivery fee", "how much to deliver", "how much is delivery", "ziga", "kai wa", "isarwa", "nnyefe", "ifijise"]),
        (AskLocation, ["where are you", "where is your", "your address", "address", "location", "located", "your shop", "your store", "pick up", "pickup", "collect it", "come and buy", "where una dey", "ebee ka unu no", "ina kuke"]),
        (AskHours, ["open", "opening", "closing", "close today", "working hours", "what time", "business hours", "are you open", "una open", "sunday", "saturday", "weekend", "public holiday"]),
        (AskDiscount, ["discount", "last price", "reduce", "reduction", "cheaper", "too expensive", "too cost", "bulk price", "wholesale price", "any promo", "promo", "bargain", "drop am small"]),
        (AskRecommend, ["recommend", "suggest", "which one is best", "which is best", "best for", "good for my", "what should i buy", "what do you advise", "which food", "which one should", "help me choose"]),
        (AskInfo, ["ingredient", "ingredients", "protein", "fat", "nutrition", "feeding guide", "how much to feed", "how many grams", "how much should i feed", "what is inside", "made of", "benefit", "benefits", "tell me about", "details", "difference between", "is it good", "suitable", "expiry", "expire"]),
        (AskStock, ["in stock", "available", "do you have", "you get", "una get", "e dey", "is there", "have you got", "any left", "still have"]),
        (AskPrice, ["how much", "price", "prices", "cost", "how much is", "wetin be the price", "elo ni", "nawa ne", "ego ole", "ole ka", "rate"]),
    ];

    /// <summary>What the shopper is asking, if anything: the first enquiry whose words appear in the message, most specific first.</summary>
    public static string? EnquiryOf(string n)
    {
        foreach (var (intent, words) in Enquiries) if (Has(n, words)) return intent;
        return null;
    }

    /// <summary>A pet and life stage the shopper mentioned, for a recommendation: ("dog", "puppy") etc. Either may be null.</summary>
    public static (string? Species, string? Stage) PetOf(string n)
    {
        string? species = Has(n, "cat", "cats", "kitten", "kittens", "mage", "nwamba", "ologbo") ? "cat" : Has(n, "dog", "dogs", "puppy", "puppies", "pup", "nkita", "kare", "aja") ? "dog" : null;
        string? stage = Has(n, "puppy", "puppies", "pup", "kitten", "kittens", "small", "baby", "young") ? "young"
            : Has(n, "senior", "old", "older", "aged") ? "senior" : Has(n, "adult", "grown") ? "adult" : null;
        return (species, stage);
    }

    /// <summary>The customer is asking for a quotation (a price document), not to pay.</summary>
    public static bool IsQuote(string n) => Has(n, QuoteWords);
    /// <summary>The customer is asking where their delivery is / for the waybill.</summary>
    public static bool IsWaybill(string n) => Has(n, WaybillWords);
    public static bool IsSend(string n) => Tokens(n).Length <= 5 && (Has(n, SendWords) || IsYes(n));
    public static bool IsPay(string n) => Tokens(n).Length <= 5 && Has(n, PayWords);

    private static readonly System.Text.RegularExpressions.Regex EmailShape = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    /// <summary>The first email address typed in the message, as typed (not normalised), or null.</summary>
    public static string? ExtractEmail(string? text) { var m = EmailShape.Match(text ?? ""); return m.Success && m.Value.Length <= 120 ? m.Value : null; }
    public static bool IsGreeting(string n) => Tokens(n).Length <= 6 && Has(n, GreetingWords);

    private static readonly string[] OpeningGreetings = ["hi", "hello", "hey", "hiya", "good morning", "good afternoon", "good evening", "good day", "how far", "nnoo", "ndewo", "kedu", "sannu", "barka", "ina kwana", "salam", "assalamu alaikum", "e kaaro", "e kaasan", "e kaale"];
    /// <summary>The message OPENS with a greeting ("Hi Chewy Pet! I'd like to order some dog food"), however long the rest is. Whole words only.</summary>
    public static bool StartsWithGreeting(string n) => OpeningGreetings.Any(g => n == g || n.StartsWith(g + " ", StringComparison.Ordinal));

    // ---------------------------------------------------------------- numbers & filler

    public static readonly Dictionary<string, int> NumberWords = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["dozen"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["fifty"] = 50,
        // Igbo
        ["otu"] = 1, ["abuo"] = 2, ["ato"] = 3, ["ano"] = 4, ["ise"] = 5, ["isii"] = 6, ["asaa"] = 7, ["asato"] = 8, ["itoolu"] = 9, ["iri"] = 10,
        // Hausa
        ["daya"] = 1, ["biyu"] = 2, ["uku"] = 3, ["hudu"] = 4, ["biyar"] = 5, ["shida"] = 6, ["bakwai"] = 7, ["takwas"] = 8, ["tara"] = 9, ["goma"] = 10,
        // Yoruba
        ["okan"] = 1, ["eji"] = 2, ["meji"] = 2, ["eta"] = 3, ["meta"] = 3, ["erin"] = 4, ["merin"] = 4, ["marun"] = 5, ["aarun"] = 5, ["mefa"] = 6, ["meje"] = 7, ["mejo"] = 8, ["mesan"] = 9, ["mewa"] = 10, ["ogun"] = 20,
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
        "quote", "quotes", "quotation", "quotations", "proforma", "kwote", "email", "mail",
        // English contractions lose their apostrophe when normalised ("I'd like" -> "id like"), and the website shop's own words
        "id", "im", "ill", "ive", "wanna", "gonna", "lemme", "kindly", "from", "website", "site", "shop", "following", "items", "these", "those",
        // store names: the one line sells for every store, and a message may open by naming one ("Hi Candid Purrfect! …")
        "candid", "purrfect", "store",
        // greetings and forms of address that often open an order ("Ndewo, achọrọ m 2 rice")
        "ndewo", "nnoo", "kedu", "sannu", "barka", "salam", "assalamu", "alaikum", "imeela", "nagode", "daalu", "kwana", "wuni", "jowo", "mo", "fe", "nfe", "ra", "kaaro", "kaasan", "kaale", "egbon", "baba", "iya", "e", "ni", "kan", "dear", "boss", "bros", "brother", "sister", "aunty", "uncle", "mama", "papa", "day", "sup", "yo", "hiya", "far", "afternoon",
    };

    /// <summary>Separators between two products in one message: "2 rice and 3 beans", "rice, beans", "2 rice na 3 beans", "shinkafa 2 da wake 3".</summary>
    public static readonly string[] ItemSeparators = ["and", "plus", "na", "with", "kuma", "da", "also", "then"];
}
