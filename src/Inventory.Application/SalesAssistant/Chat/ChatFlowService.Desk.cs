using Inventory.Application.Payments;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// The enquiry desk: a shopper's questions are answered in ANY state of the sale, from live records, and every answer ends with one short line that
/// leads back to the sale (the open payment account, "shall I place it?", "how many?"). Each kind of question is one tool below; words pick the tool
/// first (free), the AI interpreter picks it only when the words could not. Nothing here invents a fee, a price or a promise: what the records don't
/// hold is handed to the team.
/// </summary>
public sealed partial class ChatFlowService
{
    private const string KZone = "zone", KAddress = "address";

    /// <param name="forced">An enquiry the AI recognised when the words did not.</param>
    /// <param name="aiProducts">Products the AI matched in the message.</param>
    private async Task<bool> HandleEnquiryAsync(Turn t, CancellationToken ct, string? forced = null, IReadOnlyList<int>? aiProducts = null)
    {
        if (await ResolveDeskAnswerAsync(t, ct)) return true;
        var intent = forced ?? ChatLexicon.EnquiryOf(t.Norm);
        if (intent is null) return false;

        var cat = await CatAsync(t, ct);
        var parsed = ProductMatcher.Split(t.Norm).Select(r => ProductMatcher.Match(r, cat)).ToList();
        // "2 bags of ALS, is it available?" is an order first: the order flow checks the stock anyway.
        if (forced is null && intent is ChatLexicon.AskStock or ChatLexicon.AskPrice && parsed.Any(p => p.Matches.Count == 1 && p.Raw.Qty is not null)) return false;

        var named = (aiProducts is { Count: > 0 } ids ? cat.Where(c => ids.Contains(c.Id)) : parsed.SelectMany(p => p.Matches.Count > 0 ? p.Matches : p.Near)).DistinctBy(p => p.Id).Take(4).ToList();
        // No product named: the one being asked about (the quantity question) or the ones already in the order are the subject.
        var subject = named.Count > 0 ? named
            : t.D.Waiting is { Kind: KQty or KStock or KWhich } w ? cat.Where(c => w.ProductIds.Contains(c.Id)).ToList()
            : cat.Where(c => t.D.Lines.Any(l => l.ProductId == c.Id)).ToList();

        Track(t, ChatEventKinds.Enquiry, null, intent);
        switch (intent)
        {
            case ChatLexicon.AskDelivery: await DeliveryAsync(t, ct); break;
            case ChatLexicon.AskPrice: if (!PriceAnswer(t, cat, subject)) return true; break;
            case ChatLexicon.AskStock: StockAnswer(t, cat, subject); break;
            case ChatLexicon.AskInfo: await InfoAnswerAsync(t, cat, subject, ct); break;
            case ChatLexicon.AskRecommend: await RecommendAsync(t, cat, ct); break;
            case ChatLexicon.AskHours:
                { var h = (await profile.GetAsync(ct)).OpeningHours; t.Out.Add(string.IsNullOrWhiteSpace(h) ? ChatText.Get(t.Lang, "h_hours_none") : ChatText.Get(t.Lang, "h_hours", h)); break; }
            case ChatLexicon.AskLocation:
                { var a = (await profile.GetAsync(ct)).Address; t.Out.Add(string.IsNullOrWhiteSpace(a) ? ChatText.Get(t.Lang, "l_none") : ChatText.Get(t.Lang, "l_where", a)); break; }
            case ChatLexicon.AskPayHow: t.Say("pay_how"); break;
            case ChatLexicon.AskDiscount: t.Say("discount"); break;
            case ChatLexicon.AskHuman:
                {
                    var phone = (await profile.GetAsync(ct)).Phone;
                    t.Out.Add(string.IsNullOrWhiteSpace(phone) ? ChatText.Get(t.Lang, "human_noline") : ChatText.Get(t.Lang, "human", phone));
                    Track(t, ChatEventKinds.Handoff);
                    return true;                                 // a person takes it from here: no sales nudge on top
                }
            default: return false;
        }
        await NudgeAsync(t, ct);
        return true;
    }

    // ------------------------------------------------------------------ the desk's own follow-up questions: which area, what address, add delivery?

    private async Task<bool> ResolveDeskAnswerAsync(Turn t, CancellationToken ct)
    {
        // "ADD DELIVERY" (or yes) after delivery was offered while a payment account is open: re-price the order with delivery.
        if (t.D.Offer is { } offer && t.C.State == ChatStates.AwaitingPayment && (ChatLexicon.Has(t.Norm, "add delivery", "add the delivery", "add am", "add it") || ChatLexicon.IsYes(t.Norm)))
        {
            t.D.Offer = null;
            t.D.Zone = offer;
            t.D.Waiting = new Pending { Kind = KAddress };
            t.Say("d_ask_address");
            return true;
        }
        if (t.D.Waiting is not { Kind: KZone or KAddress } w) return false;
        if (ChatLexicon.EnquiryOf(t.Norm) is { } other && other != ChatLexicon.AskDelivery) { t.D.Waiting = null; return false; }   // a new question: answer that instead

        if (w.Kind == KZone)
        {
            var zones = await ZonesAsync(ct);
            var zone = FindZone(t.Norm, zones);
            t.D.Waiting = null;
            if (zone is null)
            {
                t.Say("d_unknown_area", t.Text.Trim().Length > 40 ? t.Text.Trim()[..40] : t.Text.Trim());
                Track(t, ChatEventKinds.Handoff);
                await NudgeAsync(t, ct);
                return true;
            }
            await ZoneChosenAsync(t, zone, ct);
            return true;
        }

        // the delivery address
        var text = t.Text.Trim();
        if (text.Length < 6 || ChatLexicon.IsYes(t.Norm) || ChatLexicon.IsNo(t.Norm)) { t.Say("d_ask_address"); return true; }
        t.D.Address = text.Length > 250 ? text[..250] : text;
        t.D.Waiting = null;
        t.Say("d_address_ok", t.D.Address);
        if (t.C.State == ChatStates.AwaitingPayment)
        {
            // The open account was for the order without delivery: close it and issue one for the new total.
            if (t.C.QuotationId is int q)
                foreach (var l in await db.PaymentLinks.Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == q && l.Status == PaymentLinkStatuses.Pending).ToListAsync(ct))
                    l.Status = PaymentLinkStatuses.Cancelled;
            await db.SaveChangesAsync(ct);
            await PlaceOrderAsync(t, await CatAsync(t, ct), ct);
        }
        else if (t.D.Lines.Count > 0) await AdvanceAsync(t, ct);
        else await NudgeAsync(t, ct);
        return true;
    }

    // ------------------------------------------------------------------ tools

    private sealed record Zone(string Name, decimal Fee, string? Eta);

    private async Task<List<Zone>> ZonesAsync(CancellationToken ct) =>
        await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive).OrderBy(z => z.Fee).ThenBy(z => z.Name).Select(z => new Zone(z.Name, z.Fee, z.Eta)).ToListAsync(ct);

    private static Zone? FindZone(string norm, IReadOnlyList<Zone> zones) =>
        zones.FirstOrDefault(z => ChatLexicon.Has(norm, ChatLexicon.Normalize(z.Name)))
        ?? zones.FirstOrDefault(z => ChatLexicon.Normalize(z.Name).Split(' ').Where(w => w.Length > 3).Any(w => ChatLexicon.Has(norm, w)));

    private string ZoneLine(Turn t, Zone z) => z.Fee > 0
        ? ChatText.Get(t.Lang, "d_zone", z.Name, ChatOrderText.Naira(z.Fee), z.Eta ?? ChatText.Get(t.Lang, "d_eta_default"))
        : ChatText.Get(t.Lang, "d_zone_nofee", z.Name);

    private async Task DeliveryAsync(Turn t, CancellationToken ct)
    {
        var zones = await ZonesAsync(ct);
        if (FindZone(t.Norm, zones) is { } zone) { await ZoneChosenAsync(t, zone, ct, nudge: false); return; }
        if (zones.Count == 0) { t.Say("d_none"); t.D.Waiting = new Pending { Kind = KZone }; return; }
        var list = string.Join(", ", zones.Take(8).Select(z => z.Fee > 0 ? $"{z.Name} ({ChatOrderText.Naira(z.Fee)})" : z.Name));
        t.Say("d_list", list);
        t.D.Waiting = new Pending { Kind = KZone };
    }

    /// <summary>The shopper named an area we serve: answer the fee and, if an order is in hand, put delivery on it.</summary>
    private async Task ZoneChosenAsync(Turn t, Zone zone, CancellationToken ct, bool nudge = true)
    {
        t.Out.Add(ZoneLine(t, zone));
        if (t.C.State == ChatStates.AwaitingPayment) { t.D.Offer = zone.Name; t.Say("d_offer_paying", zone.Name); return; }
        if (t.D.Lines.Count > 0 || t.C.State == ChatStates.AwaitingConfirm)
        {
            t.D.Zone = zone.Name;
            t.Say("d_added", zone.Name, zone.Fee > 0 ? ChatOrderText.Naira(zone.Fee) : ChatText.Get(t.Lang, "d_eta_default"));
            if (string.IsNullOrWhiteSpace(t.D.Address)) { t.D.Waiting = new Pending { Kind = KAddress }; t.Say("d_ask_address"); return; }
            await AdvanceAsync(t, ct);
            return;
        }
        t.D.Zone = zone.Name;                        // remembered for the order that follows
        if (nudge) await NudgeAsync(t, ct);
    }

    /// <summary>Price of what was named; one product leads straight to "how many?". Returns false when the catalogue was shown instead (it carries its own lead).</summary>
    private bool PriceAnswer(Turn t, List<CatalogItem> cat, List<CatalogItem> subject)
    {
        if (subject.Count == 0) { CatalogueReply(t, cat); return false; }
        if (subject.Count == 1 && subject[0].Available > 0 && t.D.Lines.All(l => l.ProductId != subject[0].Id))
        {
            Interested(t, subject[0].Id);     // the nudge asks "how many?" with the price in it
            return true;
        }
        foreach (var p in subject) t.Say("p_price", p.Name, ChatOrderText.Naira(p.Price), (p.Unit is { Length: > 0 } u ? u : "unit").ToLowerInvariant());
        return true;
    }

    private void StockAnswer(Turn t, List<CatalogItem> cat, List<CatalogItem> subject)
    {
        if (subject.Count == 0) { CatalogueReply(t, cat); return; }
        foreach (var p in subject)
        {
            if (p.Available <= 0)
            {
                t.Say("p_stock_out", p.Name);
                var tokens = p.Tokens.Where(w => w.Length > 2).ToHashSet();
                var alt = cat.Where(c => c.Id != p.Id && c.Available > 0).OrderByDescending(c => c.Tokens.Count(tokens.Contains)).FirstOrDefault(c => c.Tokens.Any(tokens.Contains));
                if (alt is not null) t.Say("p_alt", alt.Name, ChatOrderText.Naira(alt.Price));
            }
            else if (p.Available <= 5) t.Say("p_stock_few", p.Name, p.Available);
            else t.Say("p_stock_yes", p.Name);
        }
        var one = subject.Where(p => p.Available > 0).ToList();
        if (one.Count == 1 && t.D.Lines.All(l => l.ProductId != one[0].Id)) Interested(t, one[0].Id);
    }

    private async Task InfoAnswerAsync(Turn t, List<CatalogItem> cat, List<CatalogItem> subject, CancellationToken ct)
    {
        if (subject.Count == 0)
        {
            if (ChatLexicon.PetOf(t.Norm) is { Species: not null } or { Stage: not null }) { await RecommendAsync(t, cat, ct); return; }
            CatalogueReply(t, cat); return;
        }
        var item = subject[0];
        var p = await db.Products.AsNoTracking().Where(x => x.Id == item.Id)
            .Select(x => new { x.Description, x.NutritionSummary, x.Species, x.LifeStage, x.ProteinPct, x.FatPct, x.FiberPct, x.MoisturePct, x.FeedingGuide }).FirstAsync(ct);
        var said = 0;
        if (!string.IsNullOrWhiteSpace(p.Description)) { t.Out.Add(p.Description!.Trim()); said++; }
        else if (!string.IsNullOrWhiteSpace(p.NutritionSummary)) { t.Out.Add(p.NutritionSummary!.Trim()); said++; }
        var who = string.Join(", ", new[] { p.Species, p.LifeStage }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (who.Length > 0) { t.Say("i_for", who); said++; }
        var analysis = string.Join(", ", new (string Name, decimal? V)[] { ("Protein", p.ProteinPct), ("Fat", p.FatPct), ("Fibre", p.FiberPct), ("Moisture", p.MoisturePct) }
            .Where(a => a.V is not null).Select(a => $"{a.Name} {a.V:0.#}%"));
        if (analysis.Length > 0) { t.Say("i_analysis", analysis); said++; }
        var feed = (p.FeedingGuide ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5).ToList();
        if (feed.Count > 0) { t.Say("i_feed", string.Join('\n', feed)); said++; }
        if (said == 0) { t.Say("i_none", item.Name); Track(t, ChatEventKinds.Handoff); }
        if (item.Available > 0 && t.D.Lines.All(l => l.ProductId != item.Id)) Interested(t, item.Id);
    }

    /// <summary>"Which is best for my puppy?": in-stock products that suit the pet and stage, best match first.</summary>
    private async Task RecommendAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        var (species, stage) = ChatLexicon.PetOf(t.Norm);
        if (species is null && stage is null) { t.Say("r_none"); return; }
        var ids = cat.Where(c => c.Available > 0).Select(c => c.Id).ToList();
        var facts = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Species, x.LifeStage }).ToDictionaryAsync(x => x.Id, ct);
        int Score(CatalogItem c)
        {
            var f = facts.GetValueOrDefault(c.Id);
            var text = ChatLexicon.Normalize($"{c.Name} {f?.Species} {f?.LifeStage}");
            var s = 0;
            if (species == "cat" && ChatLexicon.Has(text, "cat", "kitten", "feline")) s += 3;
            if (species == "dog" && ChatLexicon.Has(text, "dog", "puppy", "canine", "chewypets")) s += 3;
            if (species == "dog" && ChatLexicon.Has(text, "cat", "kitten")) s -= 5;
            if (species == "cat" && ChatLexicon.Has(text, "dog", "puppy")) s -= 5;
            if (stage == "young" && ChatLexicon.Has(text, "puppy", "kitten", "junior", "starter")) s += 3;
            if (stage == "adult" && ChatLexicon.Has(text, "adult", "als", "all life stages", "all life stage", "maintenance")) s += 3;
            if (stage == "senior" && ChatLexicon.Has(text, "senior", "mature", "als", "all life stages")) s += 3;
            if (stage is not null && ChatLexicon.Has(text, "als", "all life stages", "all life stage")) s += 1;
            return s;
        }
        var picks = cat.Where(c => c.Available > 0).Select(c => (c, s: Score(c))).Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.c.Price).Take(3).Select(x => x.c).ToList();
        if (picks.Count == 0) { t.Say("r_none"); return; }
        var label = (stage, species) switch
        {
            ("young", "cat") => "your kitten", ("young", _) => "your puppy", ("senior", _) => "an older pet", (_, "cat") => "your cat", _ => "your dog",
        };
        t.Out.Add(ChatText.Get(t.Lang, "r_intro", label) + "\n" + string.Join('\n', picks.Select(p => $"• {p.Name}: {ChatOrderText.Naira(p.Price)}")));
        if (t.D.Lines.All(l => l.ProductId != picks[0].Id)) Interested(t, picks[0].Id);
    }

    /// <summary>The shopper is weighing up this product: ask "how many?" next, and keep coming back to it.</summary>
    private static void Interested(Turn t, int productId)
    {
        t.D.Waiting = new Pending { Kind = KQty, ProductIds = [productId] };
        t.D.Interest = productId;
    }

    // ------------------------------------------------------------------ the line after every answer: back to the sale

    private async Task NudgeAsync(Turn t, CancellationToken ct)
    {
        var cat = await CatAsync(t, ct);
        if (t.D.Waiting is { } w)
        {
            var items = cat.Where(c => w.ProductIds.Contains(c.Id)).ToList();
            switch (w.Kind)
            {
                case KQty when items.Count == 1: t.Say("ask_qty", items[0].Name, ChatOrderText.Naira(items[0].Price)); return;
                case KWhich when items.Count > 1: t.Say("which", string.Join(" / ", items.Select(i => i.Name))); return;
                case KStock when items.Count == 1: t.Say("only_n", items[0].Name, w.Available ?? 0); return;
                case KZone or KAddress: return;          // the question itself was just asked
            }
        }
        switch (t.C.State)
        {
            case ChatStates.AwaitingPayment:
            {
                var links = t.C.QuotationId is int q
                    ? await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == q && l.Status == PaymentLinkStatuses.Pending).OrderByDescending(l => l.Id).ToListAsync(ct)
                    : [];
                if (t.D.Offer is not null || links.Count == 0) return;
                var account = links.FirstOrDefault(l => l.ExternalId != null);
                if (account is not null) t.Say("n_pay", ChatOrderText.Naira(account.Amount), account.BankName ?? "", account.AccountNumber ?? "");
                else t.Say("n_pay_link", ChatOrderText.Naira(links[0].Amount), links[0].Url);
                return;
            }
            case ChatStates.AwaitingConfirm: t.Say("n_confirm", ChatOrderText.Naira(await DraftTotalAsync(t, ct))); return;
            case ChatStates.AwaitingName: t.Say("ask_name"); return;
            case ChatStates.AwaitingPhone: t.Say("ask_phone"); return;
            case ChatStates.AwaitingConsent: t.Say("consent_ask", company.LegalName); return;
        }
        // Back to the product they were weighing up: "how many?" until they decide.
        if (t.D.Interest is int i && cat.FirstOrDefault(c => c.Id == i) is { Available: > 0 } p)
        {
            t.D.Waiting = new Pending { Kind = KQty, ProductIds = [p.Id] };
            t.Say("ask_qty", p.Name, ChatOrderText.Naira(p.Price));
            return;
        }
        if (t.D.Lines.Count > 0) t.Say("n_more");
        else t.Say("n_idle", ExampleProduct(t));
    }

    /// <summary>The draft's total as the order will be priced: items, VAT on the items, then delivery.</summary>
    private async Task<decimal> DraftTotalAsync(Turn t, CancellationToken ct)
    {
        var cat = await CatAsync(t, ct);
        var vat = (await profile.GetAsync(ct)).DefaultVatRate;
        var rows = t.D.Lines.Where(l => cat.Any(c => c.Id == l.ProductId)).Select(l => new Domain.Rules.SaleLineInput(l.ProductId, l.Qty, cat.First(c => c.Id == l.ProductId).Price));
        return Domain.Rules.DocumentCalculator.Sale(rows, 0, vat).Total + await DeliveryFeeAsync(t, ct);
    }

    private async Task<decimal> DeliveryFeeAsync(Turn t, CancellationToken ct) =>
        t.D.Zone is { } z ? await db.DeliveryZones.AsNoTracking().Where(x => x.IsActive && x.Name == z).Select(x => x.Fee).FirstOrDefaultAsync(ct) : 0;
}
