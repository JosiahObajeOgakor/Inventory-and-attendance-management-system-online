using Inventory.Domain.Entities;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>Trade registration on WhatsApp: the fill-in form, the answers about where an application stands, and the website route for web chat.</summary>
public static partial class ChatText
{
    /// <summary>The form's labels, in the order they're asked. The parser reads "Label: value" lines and doesn't care about case or extra spaces.</summary>
    public const string TradeForm =
        "TRADE FORM\nBusiness name: \nYour name: \nEmail: \nShop address: \nCAC number: \nTIN: \nWholesaler or Distributor: \nAbout your business: ";

    private static readonly Dictionary<string, Dictionary<string, string>> Trade = new()
    {
        [ChatLanguages.English] = new()
        {
            ["t_intro"] = "Trade prices are for registered pet shops, wholesalers and distributors. Copy the form below, fill it in and send it back (one message):",
            ["t_after"] = "Prefer a web form with document upload? {0}",
            ["t_web"] = "Apply for trade prices here (takes two minutes, attach your CAC certificate): {0}",
            ["t_received"] = "Received, {0}. Your application for {1} prices is with our managers. We'll message you once it's decided, usually within one working day. Until then you'll see retail prices.",
            ["t_send_cac"] = "To speed it up, email a photo of your CAC certificate to {0}, or upload it here: {1}",
            ["t_missing"] = "Almost there. Please fill in: {0}. Send the whole form again with those lines filled.",
            ["t_bad"] = "{0} Please correct it and send the form again.",
            ["t_pending"] = "Your trade application for {0} is still with our managers. We'll message you as soon as it's decided.",
            ["t_approved"] = "{0} is approved for {1} prices. Just order as usual and you'll see your prices.",
            ["t_suspended"] = "Your trade account is on hold. Please call us so we can sort it out.",
            ["t_rejected_again"] = "Your last application wasn't approved{0}. You can apply again: send the form with updated details.",
        },
        [ChatLanguages.Pidgin] = new()
        {
            ["t_intro"] = "Trade price na for registered pet shop, wholesaler and distributor. Copy the form wey dey below, fill am, send am back (one message):",
            ["t_after"] = "You prefer web form wey you fit upload document? {0}",
            ["t_web"] = "Apply for trade price for here (e no pass two minutes, attach your CAC certificate): {0}",
            ["t_received"] = "We don receive am, {0}. Your application for {1} price dey with our managers. We go message you once dem decide, usually inside one working day. Till then na retail price you go see.",
            ["t_send_cac"] = "Make e fast, email photo of your CAC certificate to {0}, or upload am here: {1}",
            ["t_missing"] = "E remain small. Abeg fill: {0}. Send the whole form again with those lines filled.",
            ["t_bad"] = "{0} Abeg correct am and send the form again.",
            ["t_pending"] = "Your trade application for {0} still dey with our managers. We go message you once dem decide.",
            ["t_approved"] = "{0} don get approval for {1} price. Just order as you dey do, you go see your price.",
            ["t_suspended"] = "Your trade account dey on hold. Abeg call us make we sort am.",
            ["t_rejected_again"] = "Your last application no pass{0}. You fit apply again: send the form with new details.",
        },
        [ChatLanguages.Igbo] = new()
        {
            ["t_intro"] = "Ọnụahịa trade bụ maka ụlọ ahịa anụ ụlọ, ndị wholesaler na ndị distributor edebanyere aha. Detuo fọm dị n'okpuru, dejupụta ya, zighachi ya (otu ozi):",
            ["t_after"] = "Ị chọrọ fọm weebụ ebe ị ga-ebugo akwụkwọ? {0}",
            ["t_web"] = "Tinye akwụkwọ maka ọnụahịa trade ebe a (ọ naghị agafe nkeji abụọ, tinye asambodo CAC gị): {0}",
            ["t_received"] = "Anatala anyị ya, {0}. Arịrịọ gị maka ọnụahịa {1} dị n'aka ndị njikwa anyị. Anyị ga-ezitere gị ozi ozugbo ha kpebiri, na-abụkarị n'ime otu ụbọchị ọrụ. Ruo mgbe ahụ, ọ bụ ọnụahịa retail ka ị ga-ahụ.",
            ["t_send_cac"] = "Ka o mee ngwa ngwa, zitere foto asambodo CAC gị na {0}, ma ọ bụ bugo ya ebe a: {1}",
            ["t_missing"] = "Ọ fọdụrụ ntakịrị. Biko dejupụta: {0}. Zighachi fọm ahụ niile.",
            ["t_bad"] = "{0} Biko dozie ya ma zighachi fọm ahụ.",
            ["t_pending"] = "Arịrịọ trade gị maka {0} ka dị n'aka ndị njikwa anyị. Anyị ga-ezitere gị ozi ozugbo ha kpebiri.",
            ["t_approved"] = "Akwadola {0} maka ọnụahịa {1}. Naanị nye iwu dị ka ị na-eme, ị ga-ahụ ọnụahịa gị.",
            ["t_suspended"] = "Akaụntụ trade gị kwụsịrị nwa oge. Biko kpọọ anyị ka anyị dozie ya.",
            ["t_rejected_again"] = "Akwadoghị arịrịọ gị gara aga{0}. Ị nwere ike itinye ọzọ: zite fọm ahụ na nkọwa ọhụrụ.",
        },
        [ChatLanguages.Hausa] = new()
        {
            ["t_intro"] = "Farashin trade na shagunan dabbobi, masu sayar da yawa da masu rarrabawa da aka yi wa rajista ne. Kwafi fom ɗin da ke ƙasa, cike shi, ka mayar (saƙo ɗaya):",
            ["t_after"] = "Kana son fom na yanar gizo inda za ka loda takardu? {0}",
            ["t_web"] = "Nemi farashin trade a nan (bai wuce minti biyu ba, haɗa takardar CAC ɗinka): {0}",
            ["t_received"] = "Mun karɓa, {0}. Neman farashin {1} ɗinka yana hannun manajojinmu. Za mu aiko maka da saƙo da zarar sun yanke shawara, yawanci cikin rana ɗaya ta aiki. Kafin nan farashin retail za ka gani.",
            ["t_send_cac"] = "Don hanzarta, aiko hoton takardar CAC ɗinka zuwa {0}, ko ka loda a nan: {1}",
            ["t_missing"] = "Saura kaɗan. Don Allah cike: {0}. Ka sake aiko da fom ɗin gaba ɗaya.",
            ["t_bad"] = "{0} Don Allah gyara ka sake aiko da fom ɗin.",
            ["t_pending"] = "Neman trade ɗinka na {0} yana hannun manajojinmu har yanzu. Za mu aiko maka da saƙo da zarar sun yanke shawara.",
            ["t_approved"] = "An amince wa {0} da farashin {1}. Ka yi oda kamar yadda ka saba, za ka ga farashinka.",
            ["t_suspended"] = "An dakatar da asusun trade ɗinka na ɗan lokaci. Don Allah kira mu mu gyara.",
            ["t_rejected_again"] = "Ba a amince da neman ka na ƙarshe ba{0}. Za ka iya sake nema: aiko da fom ɗin da sabbin bayanai.",
        },
        [ChatLanguages.Yoruba] = new()
        {
            ["t_intro"] = "Iye owó trade wà fún ṣọ́ọ̀bù ẹranko, wholesaler àti distributor tí a forúkọ sílẹ̀. Da fọ́ọ̀mù ìsàlẹ̀ kọ, kún un, kí o fi ránṣẹ́ padà (ìfiránṣẹ́ kan):",
            ["t_after"] = "Ṣé o fẹ́ fọ́ọ̀mù orí ayélujára tí o lè gbé ìwé sí? {0}",
            ["t_web"] = "Béèrè fún iye owó trade níbí (kò ju ìṣẹ́jú méjì lọ, so ìwé-ẹ̀rí CAC rẹ mọ́ ọn): {0}",
            ["t_received"] = "A ti gbà á, {0}. Ìbéèrè rẹ fún iye owó {1} wà lọ́wọ́ àwọn alákòóso wa. A ó fi ìfiránṣẹ́ ránṣẹ́ sí ọ ní kété tí wọ́n bá pinnu, nígbà gbogbo láàárín ọjọ́ iṣẹ́ kan. Títí di ìgbà náà, iye owó retail ni o ó rí.",
            ["t_send_cac"] = "Kí ó lè yára, fi fọ́tò ìwé-ẹ̀rí CAC rẹ ránṣẹ́ sí {0}, tàbí gbé e sí ibí: {1}",
            ["t_missing"] = "Ó kù díẹ̀. Jọ̀wọ́ kún: {0}. Fi gbogbo fọ́ọ̀mù náà ránṣẹ́ padà.",
            ["t_bad"] = "{0} Jọ̀wọ́ ṣàtúnṣe rẹ̀ kí o sì tún fọ́ọ̀mù náà fi ránṣẹ́.",
            ["t_pending"] = "Ìbéèrè trade rẹ fún {0} ṣì wà lọ́wọ́ àwọn alákòóso wa. A ó fi ìfiránṣẹ́ ránṣẹ́ sí ọ ní kété tí wọ́n bá pinnu.",
            ["t_approved"] = "A ti fọwọ́ sí {0} fún iye owó {1}. Kàn máa bèèrè ọjà bí o ṣe máa ń ṣe, o ó rí iye owó rẹ.",
            ["t_suspended"] = "A ti dá àkọọ́lẹ̀ trade rẹ dúró fún ìgbà díẹ̀. Jọ̀wọ́ pè wá kí a lè yanjú rẹ̀.",
            ["t_rejected_again"] = "A kò fọwọ́ sí ìbéèrè rẹ tó kọjá{0}. O lè tún béèrè: fi fọ́ọ̀mù náà ránṣẹ́ pẹ̀lú àlàyé tuntun.",
        },
    };
}
