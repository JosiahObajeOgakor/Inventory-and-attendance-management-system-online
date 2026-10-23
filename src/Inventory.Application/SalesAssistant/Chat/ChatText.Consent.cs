using Inventory.Domain.Entities;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>Opt-in / opt-out wording and the lines every campaign message ends with. Merged into the main packs at start-up. Igala falls back to Pidgin (see ChatText).</summary>
public static partial class ChatText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Extra = new()
    {
        [ChatLanguages.English] = new()
        {
            ["consent_ask"] = "One quick thing: may {0} send you reminders when what you bought may be running low, and special offers, here on WhatsApp (and by email if we have yours)? You can stop any time by sending STOP. (yes / no)",
            ["consent_yes"] = "Great, you're on the list. Send STOP any time to stop.",
            ["consent_no"] = "No problem, I won't send reminders or offers. Type SUBSCRIBE any time if you change your mind.",
            ["optout_done"] = "Done. You won't get reminders or offers from us any more. Type SUBSCRIBE if you change your mind.",
            ["optin_done"] = "You're subscribed again. Send STOP any time to stop.",
            ["stop_hint"] = "Reply STOP to stop these messages.",
            ["cta_reorder"] = "Reply YES and I'll prepare the same order again.",
            ["cta_unpaid"] = "Reply YES and I'll send you a fresh payment account.",
            ["cta_promo"] = "Tell me how many you'd like and I'll set it up.",
            ["cta_debt"] = "If you've already paid, just reply here and we'll check.",
            ["ask_phone"] = "What's your WhatsApp number? We put it on your order and use it if we need to reach you about delivery.",
            ["bad_phone"] = "That doesn't look like a phone number. Please type it like 08031234567."
        },
        [ChatLanguages.Pidgin] = new()
        {
            ["consent_ask"] = "Small thing: make {0} fit send you reminder when wetin you buy don near finish, and special offers, for here for WhatsApp (and email if we get yours)? You fit stop anytime, just send STOP. (yes / no)",
            ["consent_yes"] = "Oya, I don add you. Send STOP anytime make e stop.",
            ["consent_no"] = "No wahala, I no go send reminder or offers. Write SUBSCRIBE anytime if you change mind.",
            ["optout_done"] = "Done. You no go get reminder or offers from us again. Write SUBSCRIBE if you change mind.",
            ["optin_done"] = "You don subscribe again. Send STOP anytime make e stop.",
            ["stop_hint"] = "Reply STOP make we stop dis messages.",
            ["cta_reorder"] = "Reply YES make I prepare the same order again.",
            ["cta_unpaid"] = "Reply YES make I send you fresh account to pay.",
            ["cta_promo"] = "Tell me how many you want make I set am.",
            ["cta_debt"] = "If you don already pay, just reply for here make we check.",
            ["ask_phone"] = "Wetin be your WhatsApp number? We go put am for your order, and use am if we need reach you about delivery.",
            ["bad_phone"] = "E no resemble phone number. Abeg type am like 08031234567."
        },
        [ChatLanguages.Igbo] = new()
        {
            ["consent_ask"] = "Otu ihe ntakịrị: {0} ọ nwere ike izitere gị ncheta mgbe ihe ị zụrụ na-agwụ agwụ, na onyinye pụrụ iche, ebe a na WhatsApp (na email ma ọ bụrụ na anyị nwere nke gị)? Ị nwere ike ịkwụsị oge ọ bụla site n'iziga STOP. (ee / mba)",
            ["consent_yes"] = "Ọ dị mma, edebere gị. Zipu STOP oge ọ bụla ka ọ kwụsị.",
            ["consent_no"] = "Enweghị nsogbu, agaghị m ezitere gị ncheta ma ọ bụ onyinye. Dee SUBSCRIBE oge ọ bụla ị chọrọ ịgbanwe.",
            ["optout_done"] = "Emechala. Ị gaghị enweta ncheta ma ọ bụ onyinye n'aka anyị ọzọ. Dee SUBSCRIBE ma ịgbanwee obi gị.",
            ["optin_done"] = "Edebegharịrị gị. Zipu STOP oge ọ bụla ka ọ kwụsị.",
            ["stop_hint"] = "Zaa STOP ka anyị kwụsị ozi ndị a.",
            ["cta_reorder"] = "Zaa YES ka m kwadebe otu order ahụ ọzọ.",
            ["cta_unpaid"] = "Zaa YES ka m ziga gị akaụntụ ọhụrụ ịkwụ ụgwọ.",
            ["cta_promo"] = "Gwa m ole ka ị chọrọ ka m guzobe ya.",
            ["cta_debt"] = "Ọ bụrụ na ị kwụziela, zaa ebe a ka anyị lelee.",
            ["ask_phone"] = "Gịnị bụ nọmba WhatsApp gị? Anyị na-etinye ya n'order gị, ma were ya kpọtụrụ gị maka nnyefe ma ọ dị mkpa.",
            ["bad_phone"] = "Nke ahụ adịghị ka nọmba ekwentị. Biko dee ya dịka 08031234567."
        },
        [ChatLanguages.Hausa] = new()
        {
            ["consent_ask"] = "Wani abu ɗan taƙaitacce: shin {0} za su iya aiko maka tunatarwa lokacin da abin da ka saya ya kusa ƙarewa, da tayi na musamman, a nan WhatsApp (da email idan muna da naka)? Za ka iya dakatarwa a kowane lokaci ta aika STOP. (eh / a'a)",
            ["consent_yes"] = "To, an saka ka. Aika STOP a kowane lokaci don dakatarwa.",
            ["consent_no"] = "Babu damuwa, ba zan aiko da tunatarwa ko tayi ba. Rubuta SUBSCRIBE a kowane lokaci idan ka canza ra'ayi.",
            ["optout_done"] = "An gama. Ba za ka sake samun tunatarwa ko tayi daga gare mu ba. Rubuta SUBSCRIBE idan ka canza ra'ayi.",
            ["optin_done"] = "An sake saka ka. Aika STOP a kowane lokaci don dakatarwa.",
            ["stop_hint"] = "Ka amsa STOP don mu daina aika waɗannan saƙonni.",
            ["cta_reorder"] = "Ka amsa YES in shirya maka odar guda ɗaya kuma.",
            ["cta_unpaid"] = "Ka amsa YES in aiko maka sabon asusun biyan kuɗi.",
            ["cta_promo"] = "Gaya mini guda nawa kake so in tsara maka.",
            ["cta_debt"] = "Idan ka riga ka biya, ka amsa nan mu duba.",
            ["ask_phone"] = "Mece ce lambar WhatsApp ɗinka? Muna saka ta a odarka, kuma mu yi amfani da ita idan muna buƙatar tuntuɓarka game da isarwa.",
            ["bad_phone"] = "Wannan bai yi kama da lambar waya ba. Don Allah ka rubuta ta kamar 08031234567."
        },
        [ChatLanguages.Igala] = new(),
    };

    static ChatText()
    {
        foreach (var source in new[] { Extra, More, Desk, Trade })
            foreach (var (lang, entries) in source)
                foreach (var kv in entries) Packs[lang][kv.Key] = kv.Value;
    }
}
