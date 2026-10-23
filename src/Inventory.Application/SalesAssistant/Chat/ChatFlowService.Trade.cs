using System.Text.RegularExpressions;
using Inventory.Application.Common;
using Inventory.Application.Partners;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// Trade registration inside the chat, with no AI: "TRADE" (or asking about wholesale or distributor prices) gets a fill-in form; the filled form,
/// sent back as one message, becomes an application exactly like the website's. On WhatsApp the sender's number is the applicant's number, so
/// nobody can apply on someone else's behalf. Web chat is sent to the website form, which also takes the CAC certificate.
/// </summary>
public sealed partial class ChatFlowService
{
    [GeneratedRegex(@"^(trade|trade form)\b|\b(trade price|trade prices|wholesale price|wholesale prices|wholesaler|distributor|distributorship|wholesalers?|distributors?|distributorship|wholesale|sell (for|to) (shops|retailers|resellers)|bulk (price|prices|order|buy)|register (my )?(business|shop|store|company)|become (a|your) (partner|distributor|wholesaler))\b")]
    private static partial Regex TradeAsk();

    private static readonly (string Key, string[] Labels)[] FormFields =
    [
        ("business", ["business name", "business", "shop name", "company name"]),
        ("contact", ["your name", "name", "contact name", "contact"]),
        ("email", ["email", "e-mail", "email address"]),
        ("address", ["shop address", "address", "business address"]),
        ("cac", ["cac number", "cac", "rc number", "bn number", "cac no"]),
        ("tin", ["tin", "tax id", "tin number", "tax number"]),
        ("tier", ["wholesaler or distributor", "type", "tier", "category"]),
        ("about", ["about your business", "about", "description", "about business"]),
    ];

    private const string SiteFallback = "https://chewypetsfeeds.com";
    private string TradeLink => (company.Website is { Length: > 0 } w ? w.TrimEnd('/') : SiteFallback) + "/#trade";

    /// <summary>True when this message was about trade registration and has been answered.</summary>
    private async Task<bool> HandleTradeAsync(Turn t, CancellationToken ct)
    {
        if (partners is null) return false;
        var form = ParseTradeForm(t.Text);
        if (form is null && !TradeAsk().IsMatch(t.Norm)) return false;

        if (t.C.Channel != ChatChannels.WhatsApp) { t.Say("t_web", TradeLink); return true; }
        var phone = PhoneOf(t)!;
        var existing = await partners.ByPhoneAsync(phone, ct);
        if (form is null)
        {
            switch (existing?.Status)
            {
                case PartnerStatuses.Approved: t.Say("t_approved", existing.BusinessName, existing.ApprovedTier!.ToLowerInvariant()); CatalogueReply(t, await CatAsync(t, ct)); return true;
                case PartnerStatuses.Suspended: t.Say("t_suspended"); return true;
                case PartnerStatuses.Pending: t.Say("t_pending", existing.BusinessName); return true;
                case PartnerStatuses.Rejected: t.Say("t_rejected_again", string.IsNullOrWhiteSpace(existing.DecisionNote) ? "" : $" ({existing.DecisionNote})"); break;
                default: t.Say("t_intro"); break;
            }
            t.Out.Add(ChatText.TradeForm);
            t.Say("t_after", TradeLink);
            Track(t, ChatEventKinds.Enquiry, null, "trade");
            return true;
        }

        var missing = FormFields.Where(f => string.IsNullOrWhiteSpace(form.GetValueOrDefault(f.Key))).Select(f => f.Labels[0]).ToList();
        if (missing.Count > 0) { t.Say("t_missing", string.Join(", ", missing)); return true; }
        try
        {
            var app = new PartnerApplication(form["business"], form["contact"], phone, form["email"], form["address"], form["cac"], form["tin"], form["about"],
                form["tier"].Contains("distrib", StringComparison.OrdinalIgnoreCase) ? "Distributor" : "Wholesaler");
            var r = await partners.ApplyAsync(app, [], "whatsapp", ct);
            if (r.Status != PartnerStatuses.Pending) { t.Out.Add(r.Message); return true; }
            t.Say("t_received", First(form["contact"]), app.RequestedTier.ToLowerInvariant());
            var email = (await profile.GetAsync(ct)).Email;
            t.Say("t_send_cac", string.IsNullOrWhiteSpace(email) ? "us" : email, TradeLink);
            Track(t, ChatEventKinds.Enquiry, null, "trade-applied");
        }
        catch (BusinessRuleException ex) { t.Say("t_bad", ex.Message); }
        return true;
    }

    /// <summary>
    /// Reads "Label: value" lines. Returns null unless the message looks like the form (at least four known labels), so an ordinary
    /// sentence with a colon in it is never mistaken for an application.
    /// </summary>
    internal static Dictionary<string, string>? ParseTradeForm(string text)
    {
        var found = new Dictionary<string, string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            var label = Regex.Replace(raw[..i].Trim().TrimStart('*', '-', '•').Trim().ToLowerInvariant(), @"\s+", " ");
            var value = raw[(i + 1)..].Trim().Trim('*').Trim();
            foreach (var (key, labels) in FormFields)
                if (labels.Contains(label) && !found.ContainsKey(key)) { found[key] = value; break; }
        }
        return found.Count >= 4 ? found : null;
    }

    /// <summary>
    /// An approved trade partner writing from their registered number: "hello" gets their business name, tier, wallet and last order; "REORDER" puts
    /// that order (what's still in stock, at today's tier prices) straight into the basket for the usual summary and payment.
    /// </summary>
    private async Task<bool> HandlePartnerAsync(Turn t, CancellationToken ct)
    {
        if (t.C.Channel != ChatChannels.WhatsApp || t.C.State is not (ChatStates.Idle or ChatStates.Collecting) || t.D.Lines.Count > 0) return false;
        var reorder = t.Norm is "reorder" or "re order" or "order again" or "same as last time" or "same order";
        if (!reorder && !ChatLexicon.IsGreeting(t.Norm)) return false;
        var phone = PhoneOf(t);
        var p = phone is null ? null : await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(x => x.Phone == phone && x.Status == PartnerStatuses.Approved && x.CustomerId != null, ct);
        if (p is null) return false;

        var cat = await CatAsync(t, ct);
        var last = await db.Invoices.AsNoTracking().Where(i => i.CustomerId == p.CustomerId && i.VoidedAt == null && !i.IsSample).OrderByDescending(i => i.Id)
            .Select(i => i.Items.Select(x => new { x.ProductId, x.Quantity }).ToList()).FirstOrDefaultAsync(ct) ?? [];
        var again = last.Where(l => cat.Any(c => c.Id == l.ProductId && c.Available >= l.Quantity)).ToList();
        if (reorder && again.Count > 0)
        {
            t.D.Lines = again.Select(l => new DraftLine { ProductId = l.ProductId, Qty = l.Quantity }).ToList();
            t.D.Name ??= p.ContactName;
            t.C.State = ChatStates.Collecting;
            await AdvanceAsync(t, ct);
            return true;
        }
        t.Say("p_welcome", p.BusinessName, (p.ApprovedTier ?? "").ToLowerInvariant());
        if (again.Count > 0) t.Say("p_last", string.Join(", ", again.Select(l => $"{l.Quantity} × {cat.First(c => c.Id == l.ProductId).Name}")));
        else t.Say("p_no_last", ExampleProduct(t));
        var wallet = await db.WalletEntries.Where(w => w.TradePartnerId == p.Id && w.Status == WalletEntryStatuses.Confirmed).SumAsync(w => (decimal?)w.Amount, ct) ?? 0;
        if (wallet > 0) t.Say("p_wallet", ChatOrderText.Naira(wallet), (company.Website is { Length: > 0 } w ? w.TrimEnd('/') : SiteFallback) + "/#partner");
        return true;
    }

    private static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}
