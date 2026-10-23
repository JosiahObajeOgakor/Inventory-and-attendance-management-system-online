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
                 Sel("focus", "AI focus (for \"Write with AI from stock\")", Campaigns.CampaignIdeas.Focuses, false, false),
                 Area("message", "Message", false, "Write {name} where the customer's name should go, e.g. \"Hello {name}, ...\". Or press \"Write with AI from stock\"."),
                 Ro("recipients", "People it was queued for", "number", true), Ro("sentOn", "Sent on")],
                [S("Draft", "info", "Sent"), S("Sent", "ok"), S(AppRecordService.AwaitingApproval, "warn")],
                [A("ai", "Write with AI from stock", In("Draft"), null, false), A("count", "How many will get it?", In("Draft"), null, false), A("send", $"Send by {channel}", In("Draft"), $"Queue this now? Every opted-in customer in the audience who has a {(subject ? "email address" : "phone number")} will get it, between 07:00 and 20:30. This can't be undone.", true),
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
            "Messages that queue themselves when something happens: chase a payment that was started but not finished, win back a shopper who cancelled, thank a new customer, win back one who has gone quiet, remind one with an overdue invoice. Rules check every 5 minutes once switched on, and each person gets a given message once. Follow-ups about a customer's own order reach anyone who hasn't opted out; offers reach opted-in customers only. On WhatsApp, a customer who hasn't messaged you in the last 24 hours can only be reached with an approved template.",
            [Kind("rule", "rule", "Rules",
                [Title("Rule"), Sel("trigger", "When", AutomationService.Triggers, true, true),
                 Num("hours", "Wait how many hours first (unpaid and cancelled orders)", false, true, "2"),
                 Num("days", "After how many days (gone quiet, overdue)", false, true, "30"),
                 Text("sendAt", "Send at (optional, e.g. 10:00)", false, true),
                 Sel("channel", "Send by", ["WhatsApp", "Email", "SMS"], true, true, "WhatsApp"), Text("subject", "Email subject"),
                 Area("message", "Message", true, "Use {name} for the customer's name, {amount} for the amount, {order} for the order number and {pay} for the payment link or account (unpaid orders).")],
                [S("Off", "info", "On"), S("On", "ok", "Off"), S(AppRecordService.AwaitingApproval, "warn")],
                [A("test", "Who would get it right now?", In("Off", "On")), A("run", "Run it now", In("On"), "Check now and queue it for anyone who matches?")])]),
    ];
}