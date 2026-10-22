using System.Text.Json;
using Inventory.Domain.Entities;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// Every sentence the bot can say, per language. Replies are fixed text with {0}, {1}… placeholders — no AI writes a customer-facing sentence,
/// so wording, prices and stock numbers can never drift from the database.
/// English, Nigerian Pidgin, Igbo, Hausa and Yoruba are written out. IGALA IS NOT: a missing key falls back Igala → Pidgin → English, and the Igala
/// strings and recognition words are meant to be supplied by a native speaker through the override file (SalesAssistant:PhrasesFile), format:
/// <code>{ "igl": { "welcome": "…" }, "markers": { "igl": ["word", "phrase"] } }</code>
/// Same file can correct any other language. Placeholders must be kept as they are.
/// </summary>
public static partial class ChatText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new()
    {
        [ChatLanguages.English] = new()
        {
            ["welcome"] = "Welcome to {0}! Tell me what you want to buy and how many, for example: \"2 {1}\". Type \"products\" to see what we have in stock.",
            ["catalogue_head"] = "Here is what we have in stock right now:",
            ["catalogue_empty"] = "Nothing is in stock right now. Please check back soon.",
            ["catalogue_tail"] = "Tell me what you want and how many, e.g. \"2 {0}\".",
            ["ask_qty"] = "{0} is {1} each. How many do you want?",
            ["which"] = "Which one do you mean: {0}? Just type the name.",
            ["not_found"] = "I couldn't find \"{0}\" in our stock. Type \"products\" to see what we have.",
            ["out_of_stock"] = "Sorry, {0} is finished at the moment.",
            ["only_n"] = "We only have {1} of {0} left. Should I take {1}? Say yes, or tell me another amount.",
            ["added"] = "Added: {0}.",
            ["summary"] = "Your order:\n{0}\nTotal: {1}\nShall I generate your payment account now? (yes / no)",
            ["ask_name"] = "What name should I put on the order?",
            ["pay_instr"] = "Please transfer exactly {0} to:\nBank: {1}\nAccount number: {2}\nThis account is only for this order and works until {3}. I will confirm right here as soon as your payment lands, so you don't need to leave this chat.",
            ["pay_wait"] = "I haven't seen your payment yet. Please transfer exactly {0} to {1} account {2}. I will message you here the moment it arrives.",
            ["paid"] = "Payment received: {0}! Your order {1} is confirmed:\n{2}\nThank you for buying from {3}.",
            ["underpaid"] = "We received {0} but your order is {1}, so {2} is still missing. Please send the remaining {2} to the same account ({3}, {4}).",
            ["unfulfilled"] = "We received your payment of {0}, but {1} ran out of stock before we could finish. Our team has been alerted and will refund you or contact you shortly.",
            ["cancelled"] = "Okay, I've cancelled that. If you already sent money for it, it will still be matched to your order and I'll tell you here. Tell me what you want whenever you're ready.",
            ["order_changed"] = "No problem. Tell me what you want instead.",
            ["stale_pay"] = "You still have an unpaid order of {0}. Please pay it, or type \"cancel\" to start a new one.",
            ["expired"] = "That payment account has expired. Tell me your order again and I'll make a new one.",
            ["unclear"] = "Sorry, I didn't catch that. Tell me what you want and how many, e.g. \"2 {0}\", or type \"products\".",
            ["handoff"] = "I'm having trouble understanding. A member of our team will reply here soon. You can also type \"products\" to see what we have.",
            ["error"] = "Something went wrong on our side. Please try again in a moment.",
            ["pay_unavailable"] = "Online payment isn't available right now. Our team will contact you here to complete your order.",
            ["limit"] = "You've reached today's message limit for this chat. Please try again tomorrow.",
            ["text_only"] = "I can only read text messages. Please type what you want to buy.",
        },
        [ChatLanguages.Pidgin] = new()
        {
            ["welcome"] = "Welcome to {0}! Tell me wetin you wan buy and how many, like: \"2 {1}\". Write \"products\" make you see wetin we get for stock.",
            ["catalogue_head"] = "Na dis one we get for stock now:",
            ["catalogue_empty"] = "Nothing dey stock now. Abeg come back soon.",
            ["catalogue_tail"] = "Tell me wetin you want and how many, like \"2 {0}\".",
            ["ask_qty"] = "{0} na {1} each. How many you want?",
            ["which"] = "Which one you mean: {0}? Just write the name.",
            ["not_found"] = "I no see \"{0}\" for our stock. Write \"products\" make you see wetin we get.",
            ["out_of_stock"] = "Sorry, {0} don finish for now.",
            ["only_n"] = "We get only {1} of {0} remain. Make I put {1}? Talk yes, or tell me another number.",
            ["added"] = "I don add: {0}.",
            ["summary"] = "Your order:\n{0}\nTotal: {1}\nYou wan make I generate account for you to pay now? (yes / no)",
            ["ask_name"] = "Wetin be your name for the order?",
            ["pay_instr"] = "Abeg transfer exactly {0} go:\nBank: {1}\nAccount number: {2}\nDis account na for dis order only and e go work till {3}. As your money land I go confirm am for here, you no need comot for dis chat.",
            ["pay_wait"] = "I never see your payment yet. Abeg transfer exactly {0} go {1} account {2}. As e land I go message you for here.",
            ["paid"] = "We don see your payment: {0}! Your order {1} don confirm:\n{2}\nThank you for buying from {3}.",
            ["underpaid"] = "We receive {0} but your order na {1}, so {2} still dey miss. Abeg send the remaining {2} go the same account ({3}, {4}).",
            ["unfulfilled"] = "We receive your {0}, but {1} finish for stock before we fit complete am. We don tell our people, dem go refund you or call you soon.",
            ["cancelled"] = "Ok, I don cancel am. If you don already send money, e go still match your order and I go tell you for here. Tell me wetin you want when you ready.",
            ["order_changed"] = "No wahala. Tell me wetin you want instead.",
            ["stale_pay"] = "You still get unpaid order of {0}. Abeg pay am, or write \"cancel\" make we start new one.",
            ["expired"] = "Dat payment account don expire. Tell me your order again make I do new one.",
            ["unclear"] = "Sorry, I no hear you well. Tell me wetin you want and how many, like \"2 {0}\", or write \"products\".",
            ["handoff"] = "I dey find am hard to understand you. One person from our team go reply you for here soon. You fit still write \"products\" to see wetin we get.",
            ["error"] = "Something do for our side. Abeg try again small time.",
            ["pay_unavailable"] = "Online payment no dey available now. Our people go contact you for here to finish your order.",
            ["limit"] = "You don reach message limit for today for dis chat. Abeg try again tomorrow.",
            ["text_only"] = "I fit only read text. Abeg type wetin you wan buy.",
        },
        [ChatLanguages.Igbo] = new()
        {
            ["welcome"] = "Nnọọ na {0}! Gwa m ihe ị chọrọ ịzụta na ọnụọgụ ole, dịka: \"2 {1}\". Dee \"products\" ka ị hụ ihe anyị nwere.",
            ["catalogue_head"] = "Ndị a bụ ihe anyị nwere ugbu a:",
            ["catalogue_empty"] = "Enweghị ihe dị ugbu a. Biko lọghachi n'oge na-adịghị anya.",
            ["catalogue_tail"] = "Gwa m ihe ị chọrọ na ọnụọgụ ole, dịka \"2 {0}\".",
            ["ask_qty"] = "{0} bụ {1} otu. Ole ka ị chọrọ?",
            ["which"] = "Nke ka ị pụtara: {0}? Dee aha ya.",
            ["not_found"] = "Ahụghị m \"{0}\" n'ahịa anyị. Dee \"products\" ka ị hụ ihe anyị nwere.",
            ["out_of_stock"] = "Ndo, {0} agwụla ugbu a.",
            ["only_n"] = "Anyị nwere naanị {1} nke {0} fọdụrụ. Ka m tinye {1}? Sị ee, ma ọ bụ gwa m ọnụọgụ ọzọ.",
            ["added"] = "Etinyere m: {0}.",
            ["summary"] = "Order gị:\n{0}\nIgwe niile: {1}\nÀ chọrọ ka m mepụta akaụntụ ịkwụ ụgwọ ugbu a? (ee / mba)",
            ["ask_name"] = "Gịnị bụ aha ị chọrọ ka m tinye n'order?",
            ["pay_instr"] = "Biko bufee {0} kpọmkwem na:\nBank: {1}\nNọmba akaụntụ: {2}\nAkaụntụ a bụ naanị maka order a, ọ ga-arụ ọrụ ruo {3}. Ozugbo ego gị batara, aga m ekwenye ya ebe a, ị gaghị apụ na chat a.",
            ["pay_wait"] = "Ahụbeghị m ịkwụ ụgwọ gị. Biko bufee {0} kpọmkwem na {1} akaụntụ {2}. Ozugbo ọ bata, aga m ezi gị ozi ebe a.",
            ["paid"] = "Anara m ịkwụ ụgwọ gị: {0}! Order gị {1} kwadoro:\n{2}\nDaalụ maka ịzụta n'aka {3}.",
            ["underpaid"] = "Anara anyị {0} mana order gị bụ {1}, ya mere {2} ka fọdụrụ. Biko zipu {2} fọdụrụ na otu akaụntụ ahụ ({3}, {4}).",
            ["unfulfilled"] = "Anara anyị ụgwọ gị {0}, mana {1} agwụla n'ahịa tupu anyị emecha. Ọrụ anyị ahụla ya, ha ga-akwụghachi gị ma ọ bụ kpọọ gị n'oge na-adịghị anya.",
            ["cancelled"] = "Ọ dị mma, akagbuola m ya. Ọ bụrụ na ị ziela ego, a ga-ejikọta ya na order gị ma m ga-agwa gị ebe a. Gwa m ihe ị chọrọ mgbe ị dị njikere.",
            ["order_changed"] = "Enweghị nsogbu. Gwa m ihe ị chọrọ kama.",
            ["stale_pay"] = "Ị ka nwere order a na-akwụbeghị ụgwọ nke {0}. Biko kwụọ ya, ma ọ bụ dee \"cancel\" ka anyị malite nke ọhụrụ.",
            ["expired"] = "Akaụntụ ịkwụ ụgwọ ahụ agwụla. Gwa m order gị ọzọ ka m mee nke ọhụrụ.",
            ["unclear"] = "Ndo, aghọtaghị m. Gwa m ihe ị chọrọ na ọnụọgụ ole, dịka \"2 {0}\", ma ọ bụ dee \"products\".",
            ["handoff"] = "Ọ na-esiri m ike ịghọta. Otu onye n'ime ndị otu anyị ga-aza gị ebe a n'oge na-adịghị anya. Ị nwekwara ike dee \"products\" ka ị hụ ihe anyị nwere.",
            ["error"] = "Nsogbu dapụrụ n'akụkụ anyị. Biko nwaa ọzọ n'oge na-adịghị anya.",
            ["pay_unavailable"] = "Ịkwụ ụgwọ n'ịntanetị adịghị ugbu a. Ndị otu anyị ga-akpọtụrụ gị ebe a ka e mechaa order gị.",
            ["limit"] = "Ị erutela oke ozi nke taa maka chat a. Biko nwaa echi.",
            ["text_only"] = "Enwere m ike ịgụ ozi e dere naanị. Biko dee ihe ị chọrọ ịzụta.",
        },
        [ChatLanguages.Hausa] = new()
        {
            ["welcome"] = "Barka da zuwa {0}! Gaya mini abin da kake so ka saya da yawansa, misali: \"2 {1}\". Rubuta \"products\" don ganin abin da muke da shi.",
            ["catalogue_head"] = "Ga abin da muke da shi a yanzu:",
            ["catalogue_empty"] = "Babu kaya a yanzu. Don Allah ka sake dubawa nan gaba.",
            ["catalogue_tail"] = "Gaya mini abin da kake so da yawansa, misali \"2 {0}\".",
            ["ask_qty"] = "{0}: {1} kowanne ɗaya. Guda nawa kake so?",
            ["which"] = "Wanne kake nufi: {0}? Rubuta sunansa.",
            ["not_found"] = "Ban sami \"{0}\" a kayanmu ba. Rubuta \"products\" don ganin abin da muke da shi.",
            ["out_of_stock"] = "Yi hakuri, {0} ya ƙare a yanzu.",
            ["only_n"] = "Saura {1} kawai na {0}. In saka {1}? Ka ce eh, ko ka gaya mini wani adadi.",
            ["added"] = "An ƙara: {0}.",
            ["summary"] = "Odarka:\n{0}\nJimilla: {1}\nShin in ƙirƙiri maka asusun biyan kuɗi yanzu? (eh / a'a)",
            ["ask_name"] = "Wane suna zan sa a odar?",
            ["pay_instr"] = "Don Allah ka tura daidai {0} zuwa:\nBanki: {1}\nLambar asusu: {2}\nWannan asusun na wannan odar ne kawai kuma yana aiki har {3}. Da zarar kuɗinka ya shiga zan tabbatar a nan, ba sai ka bar wannan chat ba.",
            ["pay_wait"] = "Ban ga biyanka ba tukuna. Don Allah ka tura daidai {0} zuwa {1} asusu {2}. Da zarar ya shiga zan aiko maka saƙo a nan.",
            ["paid"] = "An karɓi biyanka: {0}! An tabbatar da odarka {1}:\n{2}\nGodiya da siyayya daga {3}.",
            ["underpaid"] = "Mun karɓi {0} amma odarka {1} ce, don haka saura {2}. Don Allah ka tura sauran {2} zuwa asusun ɗaya ({3}, {4}).",
            ["unfulfilled"] = "Mun karɓi kuɗinka {0}, amma {1} ya ƙare kafin mu kammala. Mun sanar da ma'aikatanmu, za su mayar maka da kuɗi ko su tuntuɓe ka nan ba da daɗewa ba.",
            ["cancelled"] = "To, na soke shi. Idan ka riga ka tura kuɗi, za a haɗa shi da odarka kuma zan gaya maka a nan. Ka gaya mini abin da kake so idan ka shirya.",
            ["order_changed"] = "Babu damuwa. Gaya mini abin da kake so maimakon haka.",
            ["stale_pay"] = "Har yanzu kana da odar da ba a biya ba ta {0}. Don Allah ka biya, ko ka rubuta \"cancel\" mu fara sabuwa.",
            ["expired"] = "Wannan asusun biyan kuɗi ya ƙare. Sake gaya mini odarka in yi sabo.",
            ["unclear"] = "Yi hakuri, ban fahimta ba. Gaya mini abin da kake so da yawansa, misali \"2 {0}\", ko ka rubuta \"products\".",
            ["handoff"] = "Ina fama da fahimtar ka. Wani daga cikin ma'aikatanmu zai amsa maka a nan nan ba da daɗewa ba. Za ka iya rubuta \"products\" don ganin abin da muke da shi.",
            ["error"] = "Wani abu ya faru a wajenmu. Don Allah ka sake gwadawa nan da ɗan lokaci.",
            ["pay_unavailable"] = "Biyan kuɗi ta intanet bai samu a yanzu ba. Ma'aikatanmu za su tuntuɓe ka a nan don kammala odarka.",
            ["limit"] = "Ka kai iyakar saƙonnin yau a wannan chat. Don Allah ka sake gwadawa gobe.",
            ["text_only"] = "Zan iya karanta saƙon rubutu kawai. Don Allah ka rubuta abin da kake so ka saya.",
        },
        [ChatLanguages.Yoruba] = new(),
        // Igala: intentionally empty. Falls back to Pidgin, then English, until a native speaker supplies the strings via the override file.
        [ChatLanguages.Igala] = new(),
    };

    private static readonly object Gate = new();

    /// <summary>The sentence for <paramref name="key"/> in <paramref name="lang"/>, falling back Igala → Pidgin → English. A broken override placeholder never throws.</summary>
    public static string Get(string lang, string key, params object[] args)
    {
        string? tpl = null;
        foreach (var l in Chain(lang)) if (Packs.TryGetValue(l, out var p) && p.TryGetValue(key, out tpl)) break;
        tpl ??= key;
        try { return string.Format(tpl, args); } catch (FormatException) { return tpl; }
    }

    private static IEnumerable<string> Chain(string lang)
    {
        yield return lang;
        if (lang == ChatLanguages.Igala) yield return ChatLanguages.Pidgin;
        yield return ChatLanguages.English;
    }

    /// <summary>Loads the optional native-speaker override file. Missing or malformed files are ignored (the built-in text keeps working).</summary>
    public static void LoadOverrides(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            lock (Gate)
            {
                foreach (var lang in doc.RootElement.EnumerateObject())
                {
                    if (lang.Name == "markers" && lang.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var m in lang.Value.EnumerateObject().Where(m => m.Value.ValueKind == JsonValueKind.Array))
                            ChatLexicon.LanguageMarkers[m.Name] = m.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => ChatLexicon.Normalize(e.GetString())).Where(s => s.Length > 0).ToArray();
                        continue;
                    }
                    if (lang.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!Packs.TryGetValue(lang.Name, out var pack)) Packs[lang.Name] = pack = new();
                    foreach (var kv in lang.Value.EnumerateObject().Where(kv => kv.Value.ValueKind == JsonValueKind.String)) pack[kv.Name] = kv.Value.GetString()!;
                }
            }
        }
        catch (JsonException) { /* keep the built-in text */ }
    }

    /// <summary>For tests and the admin: every key defined in English (the reference set a translation must cover).</summary>
    public static IReadOnlyCollection<string> Keys => Packs[ChatLanguages.English].Keys;
    public static bool Covers(string lang, string key) => Packs.TryGetValue(lang, out var p) && p.ContainsKey(key);
}
