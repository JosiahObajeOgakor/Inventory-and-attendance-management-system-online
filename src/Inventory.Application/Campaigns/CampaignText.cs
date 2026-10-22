using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;

namespace Inventory.Application.Campaigns;

/// <summary>
/// Fallback wording for campaign messages when the AI is off, over budget, or wrote something that failed validation.
/// Placeholders — {name} {product} {qty} {days} {date} {stock} {price} {total} {runout} {last} {company} — are filled from the database for each customer, so even these
/// are specific to the person and rotate by day. Igala falls back to Pidgin until a native speaker supplies wording (same override idea as ChatText).
/// </summary>
public static class CampaignText
{
    public const string Reorder = "reorder", ReorderUrgent = "reorder-urgent", Unpaid = "unpaid", Debt = "debt", Promo = "promo", DropOff = "dropoff";
    public static readonly string[] Kinds = [Reorder, ReorderUrgent, Unpaid, Debt, Promo, DropOff];

    /// <summary>Situation → what the AI is told is going on (never customer data, only the shape of the message).</summary>
    public static string Situation(string kind) => kind switch
    {
        Reorder => "The customer bought a pet-feed product some time ago and has probably used it up by now. We have it in stock today. Gently remind them and offer to prepare the same order again.",
        ReorderUrgent => "The customer bought a pet-feed product some time ago and has probably used it up. Our stock of it is getting low, so it may run out soon. Remind them kindly, mention that stock is low, and offer to prepare the same order again.",
        Unpaid => "The customer started an order but never paid, and the items are still available today. Nudge them kindly and offer a fresh payment account.",
        Debt => "The customer has an amount still outstanding on their account past its due date. Remind them politely and without pressure.",
        DropOff => "The customer was choosing products in our chat recently and left before ordering. The items are in stock today. Remind them warmly of what they picked and offer to put the order together, without pressure.",
        Promo => "The customer has bought from us before. A product they might like is in stock today. Tell them about it warmly, tied to what they bought last, without any discount or claim not given.",
        _ => "A friendly note from the shop.",
    };

    public static string[] Variants(string kind, string lang)
    {
        if (lang == ChatLanguages.Igala) lang = ChatLanguages.Pidgin;
        return (lang, kind) switch
        {
            (ChatLanguages.English, Reorder) => [
                "Hi {name}! It's been {days} days since you picked up {qty} × {product} from {company}. If it's running low, we have it in stock today at {price} each.",
                "Good morning {name}, a quick one from {company}: your {product} from {date} should be about finished by now. We've got it ready whenever you are." ],
            (ChatLanguages.English, ReorderUrgent) => [
                "Hi {name}! You bought {qty} × {product} {days} days ago, so you may be nearly out. Our stock is getting low (only {stock} left today), so it's worth topping up before it runs out.",
                "{name}, a heads-up from {company}: {product} is moving fast and we're down to {stock}. If you're due a refill from {date}, now is a good time." ],
            (ChatLanguages.English, Unpaid) => [
                "Hi {name}, your order of {product} ({total}) from {days} day(s) ago was never paid for. Everything is still available today, so you can finish it in a minute.",
                "{name}, you left {product} waiting at {company}. It's still in stock, and we can hold it for you again." ],
            (ChatLanguages.English, Debt) => [
                "Hi {name}, a gentle reminder from {company}: {total} on your account has been due since {date}. Whenever you're ready, we'd appreciate it.",
                "Good morning {name}. {company} here: there's {total} outstanding on your account since {date}. Thank you for your support." ],
            (ChatLanguages.English, Promo) => [
                "Hi {name}! Since you bought {last} from us, we thought you'd like to know {product} is in stock today at {price}. We have {stock} available.",
                "{name}, {company} has {product} ready at {price}. Good company for the {last} you got last time." ],

            (ChatLanguages.English, DropOff) => [
                "Hi {name}! You were looking at {product} ({total}) in our chat {days} day(s) ago. It's still in stock at {company} today.",
                "{name}, your {product} is still waiting at {company}. We have it in stock today whenever you're ready." ],

            (ChatLanguages.Pidgin, Reorder) => [
                "Hi {name}! Na {days} days since you collect {qty} × {product} from {company}. If e don dey finish, we get am for stock today, {price} each.",
                "Good morning {name}, small thing from {company}: the {product} wey you buy for {date} suppose don almost finish. We don ready if you ready." ],
            (ChatLanguages.Pidgin, ReorderUrgent) => [
                "Hi {name}! You buy {qty} × {product} {days} days ago, so e fit don almost finish. Our stock don dey low (na only {stock} remain today), so better top up before e finish.",
                "{name}, make I hold you small: {product} dey waka fast for {company} and we don reach {stock}. If you don need refill from {date}, now sef good." ],
            (ChatLanguages.Pidgin, Unpaid) => [
                "Hi {name}, your order of {product} ({total}) from {days} day(s) ago never pay. Everything still dey today, you fit finish am for one minute.",
                "{name}, you leave {product} for {company} and e never pay. E still dey stock, we fit hold am for you again." ],
            (ChatLanguages.Pidgin, Debt) => [
                "Hi {name}, small reminder from {company}: {total} for your account don reach due since {date}. Abeg when you fit, make we settle am.",
                "Good morning {name}. {company} dey here: {total} still dey outstanding for your account since {date}. Thank you for your support." ],
            (ChatLanguages.Pidgin, Promo) => [
                "Hi {name}! Since you buy {last} from us, we think say you go like to know say {product} dey stock today, {price}. We get {stock}.",
                "{name}, {company} get {product} ready, {price}. E go match the {last} wey you buy before." ],

            (ChatLanguages.Pidgin, DropOff) => [
                "Hi {name}! You bin dey check {product} ({total}) for our chat {days} day(s) ago. E still dey stock for {company} today.",
                "{name}, the {product} wey you pick still dey wait for you for {company}. E dey stock today anytime you ready." ],

            (ChatLanguages.Igbo, Reorder) => [
                "Ndewo {name}! Ọ bụ ụbọchị {days} kemgbe ị zụtara {qty} × {product} n'aka {company}. Ọ bụrụ na ọ na-agwụ, anyị nwere ya taa na {price} otu.",
                "Ụtụtụ ọma {name}, obere ozi si {company}: {product} i zụtara na {date} kwesịrị ịdị nso ịgwụ. Anyị dịla njikere mgbe ị dị njikere." ],
            (ChatLanguages.Igbo, ReorderUrgent) => [
                "Ndewo {name}! Ị zụtara {qty} × {product} ụbọchị {days} gara aga, ya mere ọ nwere ike ịdị nso ịgwụ. Ngwongwo anyị na-ebelata (naanị {stock} fọdụrụ taa), ya mere ọ ga-adị mma ịtinyekwu tupu ọ gwụọ.",
                "{name}, ịdọ aka na ntị si {company}: {product} na-ere ngwa ngwa, anyị adaala na {stock}. Ọ bụrụ na ị chọrọ ịtinyekwu kemgbe {date}, ugbu a dị mma." ],
            (ChatLanguages.Igbo, Unpaid) => [
                "Ndewo {name}, order gị nke {product} ({total}) ụbọchị {days} gara aga ka akwụbeghị ụgwọ. Ihe niile ka dị taa, ị nwere ike imecha ya n'otu nkeji.",
                "{name}, ị hapụrụ {product} na {company}. Ọ ka dị n'ahịa, anyị nwere ike idowe ya gị ọzọ." ],
            (ChatLanguages.Igbo, Debt) => [
                "Ndewo {name}, ncheta dị nro si {company}: {total} n'akaụntụ gị bụ ihe e kwesịrị ịkwụ kemgbe {date}. Mgbe ọ dị gị mma, anyị ga-ekele gị.",
                "Ụtụtụ ọma {name}. {company} ka nke a: {total} ka fọdụrụ n'akaụntụ gị kemgbe {date}. Daalụ maka nkwado gị." ],
            (ChatLanguages.Igbo, Promo) => [
                "Ndewo {name}! Ebe ọ bụ na ị zụtara {last} n'aka anyị, anyị chere na ị ga-masị ịmara na {product} dị taa na {price}. Anyị nwere {stock}.",
                "{name}, {company} nwere {product} njikere na {price}. Ọ dabara nke ọma na {last} i zụrụ n'oge gara aga." ],

            (ChatLanguages.Igbo, DropOff) => [
                "Ndewo {name}! Ị na-ele {product} ({total}) na chat anyị ụbọchị {days} gara aga. Ọ ka dị n'ahịa na {company} taa.",
                "{name}, {product} ị họọrọ ka na-eche gị na {company}. Ọ dị taa mgbe ọ bụla ị dị njikere." ],

            (ChatLanguages.Hausa, Reorder) => [
                "Sannu {name}! Kwanaki {days} ke nan da ka ɗauki {qty} × {product} daga {company}. Idan yana ƙarewa, muna da shi a yau a {price} kowanne.",
                "Barka da safiya {name}, ɗan saƙo daga {company}: {product} ɗin da ka saya ranar {date} ya kamata ya kusa ƙarewa yanzu. A shirye muke duk lokacin da ka shirya." ],
            (ChatLanguages.Hausa, ReorderUrgent) => [
                "Sannu {name}! Ka sayi {qty} × {product} kwanaki {days} da suka wuce, don haka yana iya kusa ƙarewa. Kayanmu yana raguwa ({stock} kawai suka rage yau), don haka ya fi kyau ka ƙara kafin ya ƙare.",
                "{name}, sanarwa daga {company}: {product} yana tafiya da sauri kuma mun sauko zuwa {stock}. Idan ka kusa buƙatar sabo tun daga {date}, yanzu ne lokaci mai kyau." ],
            (ChatLanguages.Hausa, Unpaid) => [
                "Sannu {name}, odarka ta {product} ({total}) ta kwanaki {days} da suka wuce ba a biya ta ba. Komai yana nan yau, za ka iya kammalawa cikin minti ɗaya.",
                "{name}, ka bar {product} a {company}. Yana nan a kaya, za mu iya riƙe maka shi kuma." ],
            (ChatLanguages.Hausa, Debt) => [
                "Sannu {name}, tunatarwa mai laushi daga {company}: {total} a asusunka ya kai lokacin biya tun {date}. Duk lokacin da ka samu dama, za mu yi godiya.",
                "Barka da safiya {name}. {company} ce: akwai {total} da ba a biya ba a asusunka tun {date}. Na gode da goyon bayanka." ],
            (ChatLanguages.Hausa, Promo) => [
                "Sannu {name}! Tun da ka sayi {last} daga wurinmu, mun ga zai yi maka daɗi ka san cewa {product} yana nan yau a {price}. Muna da {stock}.",
                "{name}, {company} na da {product} a shirye a {price}. Yana tafiya da {last} ɗin da ka saya a baya." ],
            (ChatLanguages.Hausa, DropOff) => [
                "Sannu {name}! Kana duba {product} ({total}) a cikin hirarmu kwanaki {days} da suka wuce. Yana nan a kaya a {company} yau.",
                "{name}, {product} ɗin da ka zaɓa yana jiranka a {company}. Yana nan yau duk lokacin da ka shirya." ],
            _ => lang == ChatLanguages.English ? ["Hi {name}, a quick note from {company}."] : Variants(kind, ChatLanguages.English),
        };
    }

    /// <summary>The closing lines every campaign message gets, in code, so the AI can never drop the unsubscribe hint.</summary>
    public static string Closing(string kind, string lang) => kind switch
    {
        Reorder or ReorderUrgent => ChatText.Get(lang, "cta_reorder") + "\n" + ChatText.Get(lang, "stop_hint"),
        Unpaid => ChatText.Get(lang, "cta_unpaid") + "\n" + ChatText.Get(lang, "stop_hint"),
        Debt => ChatText.Get(lang, "cta_debt") + "\n" + ChatText.Get(lang, "stop_hint"),
        Promo => ChatText.Get(lang, "cta_promo") + "\n" + ChatText.Get(lang, "stop_hint"),
        DropOff => ChatText.Get(lang, "cta_dropoff") + "\n" + ChatText.Get(lang, "stop_hint"),
        _ => ChatText.Get(lang, "stop_hint"),
    };
}
