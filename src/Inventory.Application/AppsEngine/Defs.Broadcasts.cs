using Inventory.Domain;
using static Inventory.Application.AppsEngine.B;

namespace Inventory.Application.AppsEngine;

/// <summary>Email, SMS and WhatsApp broadcasts and automation rules. They reach only customers who opted in (see <see cref="BroadcastQueue"/>).</summary>
public static class BroadcastApps
{
    private static AppDef Campaign(string key, string name, string channel, string intro, bool subject) =>
        new(key, name, "admin", intro,
            [Kind("campaign", "campaign", "Campaigns",
                [Title("Campaign"), .. (subject ? new[] { Text("subject", "Email subject", true, true) } : Array.Empty<FieldDef>()), Sel("audience", "Who gets it", BroadcastQueue.Audiences, true, true, BroadcastQueue.Audiences[0]),
                 Customers("customers", "Selected customers", "Used when \"Who gets it\" is Selected customers. Only those who agreed to messages are sent to."),
                 Area("message", "Message", true, "Write {name} where the customer's name should go, e.g. \"Hello {name}, ...\"."),
                 Ro("recipients", "People it was queued for", "number", true), Ro("sentOn", "Sent on")],
                [S("Draft", "info", "Sent"), S("Sent", "ok")],
                [A("count", "How many will get it?", In("Draft"), null, false), A("send", $"Send by {channel}", In("Draft"), $"Queue this now? Every opted-in customer in the audience who has a {(subject ? "email address" : "phone number")} will get it, between 07:00 and 20:30. This can't be undone.", true),
                 A("report", "Delivery report", In("Sent"))],
                hint: $"Only customers who opted in are messaged, and each message carries an unsubscribe link.")]);

    public static readonly AppDef[] All =
    [
        Campaign("email-marketing", "Email Marketing", "email",
            "Newsletters and offers to the customers who agreed to hear from you. Choose who gets it, write the message, and send. Each email carries an unsubscribe link, and you can see how many arrived.", true),

        Campaign("sms-marketing", "SMS Marketing", "SMS",
            "Text the customers who agreed to hear from you. Keep it short: a long message costs more than one text, and an unsubscribe link is added to every message. The SMS provider charges per message.", false),

        Campaign("whatsapp", "WhatsApp", "WhatsApp",
            "Broadcast to opted-in customers on your WhatsApp business number. WhatsApp only lets a business start a conversation with an approved message template, so customers who haven't messaged you in the last day only get it if a broadcast template is set up (Twilio:Templates:Broadcast). The delivery report shows who got it.", false),

        new("marketing-automation", "Marketing Automation", "admin",
            "Messages that queue themselves when something happens: thank a new customer, win back one who has gone quiet, remind one with an overdue invoice. Only opted-in customers, each person gets a given message once, and rules check every hour once switched on. (The Campaigns screen does reorder reminders and offers.)",
            [Kind("rule", "rule", "Rules",
                [Title("Rule"), Sel("trigger", "When", ["A customer buys for the first time", "A customer hasn't bought for a while", "An invoice is overdue"], true, true),
                 Num("days", "After how many days (for the last two)", false, true, "30"), Sel("channel", "Send by", ["Email", "SMS", "WhatsApp"], true, true, "Email"), Text("subject", "Email subject"),
                 Area("message", "Message", true, "Use {name} for the customer's name and {amount} for what is owed (overdue rule).")],
                [S("Off", "info", "On"), S("On", "ok", "Off")],
                [A("test", "Who would get it right now?", In("Off", "On")), A("run", "Run it now", In("On"), "Check now and queue it for anyone who matches?")])]),
    ];
}