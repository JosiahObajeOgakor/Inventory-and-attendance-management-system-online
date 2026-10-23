using System.Text.Json;
using System.Text.RegularExpressions;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Payments;
using Inventory.Application.Sales;
using Inventory.Application.Trade;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Inventory.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Application.SalesAssistant.Chat;

public sealed record ChatFlowReply(IReadOnlyList<string> Messages, bool Limited = false);

/// <summary>
/// The WhatsApp sales conversation as a small state machine (Idle → Collecting → AwaitingName → AwaitingConfirm → AwaitingPayment → Idle).
/// Every decision is made from the database and from word lists; replies are fixed sentences from <see cref="ChatText"/>.
/// The AI is only consulted (<see cref="ChatAiInterpreter"/>) when the words could not be understood, at most once per message and at most
/// <see cref="SalesAssistantOptions.MaxAiCallsPerSale"/> times per sale. Customers never pick from a numbered menu: they type product names and yes/no in their own language.
/// </summary>
public sealed partial class ChatFlowService(
    IBusinessDbContext db, ChatConversationService conversations, CustomerLookupService customers, QuotationService quotations, PaymentLinkService pay,
    StockAvailabilityService stock, ChatAiInterpreter ai, ChatOrderText texts, CompanyProfileService profile, ICompanyContext company, IClock clock,
    IOptions<SalesAssistantOptions> options, ILogger<ChatFlowService> log, Inventory.Application.Campaigns.ConsentService consent,
    Inventory.Application.Email.DocumentEmailService? emails = null, Inventory.Application.Partners.TradePartnerService? partners = null)
{
    private static readonly CurrentUser SalesBot = new(0, "Sales Assistant", "SYSTEM");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SalesAssistantOptions Opt => options.Value;

    // ------------------------------------------------------------------ draft (saved on the conversation as JSON: ids and quantities only, never a price)

    private sealed class Draft
    {
        public List<DraftLine> Lines { get; set; } = [];
        /// <summary>Things the customer asked for that we haven't dealt with yet because we stopped to ask a question first.</summary>
        public List<RawLine> Todo { get; set; } = [];
        public Pending? Waiting { get; set; }
        public string? Name { get; set; }
        /// <summary>"in" or "out" once the customer has answered the opt-in question in this chat (applied to their record when it exists).</summary>
        public string? Consent { get; set; }
        /// <summary>Web chat only: the WhatsApp number the customer typed (on WhatsApp the sender's number is already known). Digits with country code.</summary>
        public string? Phone { get; set; }
        /// <summary>The customer asked for a quotation rather than to buy: the same lines, but they end in a document instead of a payment account.</summary>
        public bool Quote { get; set; }
        /// <summary>An email address the customer typed for the quotation.</summary>
        public string? Email { get; set; }
        /// <summary>The delivery zone chosen in the chat (its fee is read from the zone when the order is priced, never stored here).</summary>
        public string? Zone { get; set; }
        public string? Address { get; set; }
        /// <summary>A delivery zone offered while a payment account is already open: "ADD DELIVERY" / "yes" re-prices the order with it.</summary>
        public string? Offer { get; set; }
        /// <summary>The product the shopper last asked about (price, stock, details, a recommendation): the nudge keeps coming back to it until they decide.</summary>
        public int? Interest { get; set; }
    }
    private sealed class DraftLine { public int ProductId { get; set; } public int Qty { get; set; } }
    private sealed class Pending
    {
        public string Kind { get; set; } = "";          // which | qty | stock | zone | address
        public List<int> ProductIds { get; set; } = [];
        public int? Qty { get; set; }
        public int? Available { get; set; }
        /// <summary>How many answers didn't settle it; after two the question is dropped so the customer is never stuck.</summary>
        public int Tries { get; set; }
    }
    private const string KWhich = "which", KQty = "qty", KStock = "stock";

    private sealed class Turn
    {
        public ChatConversation C = null!;
        public Draft D = new();
        public string Text = "", Norm = "", ProfileName = "";
        public List<string> Out = [];
        public List<CatalogItem>? Cat;
        public bool AiTried;
        /// <summary>Price tier of this customer (their registered type); worked out once per message.</summary>
        public string? Tier;
        public string Lang => C.Language;
        public void Say(string key, params object[] args) => Out.Add(ChatText.Get(Lang, key, args));
    }

    // ------------------------------------------------------------------ entry point

    /// <param name="timeZone">Website chat only: the browser's time zone, kept as the visitor's rough location.</param>
    public async Task<ChatFlowReply> HandleAsync(string channel, string externalId, string profileName, string message, CancellationToken ct, string? timeZone = null)
    {
        message = (message ?? "").Trim();
        if (message.Length > Opt.MaxMessageChars) message = message[..Opt.MaxMessageChars];
        var c = await conversations.GetOrCreateAsync(channel, externalId, ct);
        var t = new Turn { C = c, Text = message, Norm = ChatLexicon.Normalize(message), ProfileName = profileName ?? "", D = LoadDraft(c) };

        if (await conversations.MessagesLast24hAsync(c.Id, ct) >= Opt.DailyMessagesPerConversation)
            return new ChatFlowReply([ChatText.Get(c.Language, "limit")], true);

        if (c.LastMessageAt == c.CreatedAt) Track(t, ChatEventKinds.Started);          // a brand-new conversation
        c.MessageCount++;
        if (c.Channel == ChatChannels.WhatsApp && c.Country is null) (c.Country, c.Network) = ChatOrigin.FromPhone(c.ExternalId);
        else if (c.Channel != ChatChannels.WhatsApp && c.Region is null) c.Region = ChatOrigin.CleanTimeZone(timeZone);
        c.Language = ChatLexicon.DetectLanguage(t.Norm, c.Language);
        if (string.IsNullOrWhiteSpace(t.D.Name) && UsableName(t.ProfileName)) t.D.Name = ExtractName(t.ProfileName);   // WhatsApp sends the customer's profile name with every message
        await conversations.AppendAsync(c.Id, ChatRoles.User, message, ct);

        var before = t.D.Lines.ToDictionary(l => l.ProductId, l => l.Qty);
        try { await StepAsync(t, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Chat flow failed in state {State}", c.State);
            t.Out.Add(ChatText.Get(c.Language, "error"));
        }

        foreach (var l in t.D.Lines.Where(l => l.Qty > before.GetValueOrDefault(l.ProductId)))       // what went into the order this turn, for the drop-off follow-up
            Track(t, ChatEventKinds.Selected, null, $"{l.ProductId}:{l.Qty}");
        c.DraftJson = JsonSerializer.Serialize(t.D, Json);
        c.LastMessageAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        if (t.Out.Count == 0) t.Out.Add(ChatText.Get(c.Language, "unclear", ExampleProduct(t)));
        await conversations.AppendAsync(c.Id, ChatRoles.Assistant, string.Join("\n\n", t.Out), ct);
        return new ChatFlowReply(t.Out);
    }

    private static Draft LoadDraft(ChatConversation c)
    {
        if (string.IsNullOrWhiteSpace(c.DraftJson)) return new Draft();
        try { return JsonSerializer.Deserialize<Draft>(c.DraftJson, Json) ?? new Draft(); } catch (JsonException) { return new Draft(); }
    }

    private async Task StepAsync(Turn t, CancellationToken ct)
    {
        if (await HandleMarketingCommandAsync(t, ct)) return;
        if (t.C.State != ChatStates.Idle && ChatLexicon.IsCancel(t.Norm)) { Cancel(t); return; }
        if (await HandleWaybillAsync(t, ct)) return;
        if (await HandleTradeAsync(t, ct)) return;        // trade registration: the form, the filled form, or where an application stands
        if (await HandlePartnerAsync(t, ct)) return;      // an approved partner: welcomed by name with their last order, REORDER repeats it
        if (await HandleQuoteIntentAsync(t, ct)) return;
        if (await HandleEnquiryAsync(t, ct)) return;      // a question is answered in any state, then the sale is picked up again
        switch (t.C.State)
        {
            case ChatStates.AwaitingPayment: await PaymentStepAsync(t, ct); break;
            case ChatStates.AwaitingName: await NameStepAsync(t, ct); break;
            case ChatStates.AwaitingConsent: await ConsentStepAsync(t, ct); break;
            case ChatStates.AwaitingPhone: await PhoneStepAsync(t, ct); break;
            case ChatStates.AwaitingConfirm: await ConfirmStepAsync(t, ct); break;
            default: await CollectAsync(t, ct); break;
        }
    }

    private async Task<List<CatalogItem>> CatAsync(Turn t, CancellationToken ct) =>
        t.Cat ??= await stock.CatalogueAsync(t.C.State is ChatStates.AwaitingConfirm or ChatStates.AwaitingPayment ? t.C.QuotationId : null, await TierAsync(t, ct), ct);

    /// <summary>
    /// Retail prices for everyone, unless the number belongs to a trade partner staff have approved (and not suspended): then the tier staff approved.
    /// The customer's type on its own is not enough; trade prices are only for verified businesses.
    /// </summary>
    private async Task<string> TierAsync(Turn t, CancellationToken ct)
    {
        if (t.Tier is not null) return t.Tier;
        var phone = PhoneOf(t);
        var tier = phone is null ? null : await db.TradePartners.AsNoTracking().Where(p => p.Phone == phone && p.Status == PartnerStatuses.Approved).Select(p => p.ApprovedTier).FirstOrDefaultAsync(ct);
        return t.Tier = PriceTiers.IsValid(tier) ? tier! : PriceTiers.Retailer;
    }

    private void Track(Turn t, string kind, decimal? amount = null, string? reference = null) =>
        db.ChatEvents.Add(new ChatEvent { At = clock.UtcNow, Channel = t.C.Channel, Kind = kind, ConversationId = t.C.Id, CustomerId = t.C.CustomerId, Amount = amount, Reference = reference });

    private static string ExampleProduct(Turn t) => (t.Cat?.FirstOrDefault(p => p.Available > 0)?.Name ?? "rice").ToLowerInvariant();

    private static void ResetSale(Turn t, bool keepName = true)
    {
        t.D = new Draft { Name = keepName ? t.D.Name : null };
        t.C.State = ChatStates.Idle;
        t.C.QuotationId = null;
        t.C.AiCalls = 0;
    }

    private void Cancel(Turn t)
    {
        var hadAccount = t.C.State == ChatStates.AwaitingPayment;
        var quotation = t.C.QuotationId;
        if (t.D.Lines.Count > 0 || hadAccount) Track(t, ChatEventKinds.Cancelled, null, quotation?.ToString());
        ResetSale(t);
        if (hadAccount) t.C.QuotationId = quotation;     // keep the link so a transfer already on its way is still matched
        t.Say("cancelled");
    }

    // ------------------------------------------------------------------ Idle / Collecting

    private async Task CollectAsync(Turn t, CancellationToken ct)
    {
        var cat = await CatAsync(t, ct);
        t.C.State = ChatStates.Collecting;

        // a hello or "show me products" while we were waiting for an answer starts afresh instead of repeating the question
        if (t.D.Waiting is not null && (ChatLexicon.IsGreeting(t.Norm) || ChatLexicon.StartsWithGreeting(t.Norm) || ChatLexicon.IsProducts(t.Norm))) t.D.Waiting = null;
        if (t.D.Waiting is not null)
        {
            if (!await ResolveWaitingAsync(t, cat, ct)) return;
            if (t.D.Todo.Count > 0 && !await ProcessAsync(t, cat, ct)) return;
            await AdvanceAsync(t, ct);
            return;
        }

        var raws = ProductMatcher.Split(t.Norm);
        var parsed = raws.Select(r => ProductMatcher.Match(r, cat)).ToList();
        if (parsed.Any(p => p.Matches.Count > 0))
        {
            t.D.Todo = raws;
            if (await ProcessAsync(t, cat, ct)) await AdvanceAsync(t, ct);
            return;
        }

        // "I have a question about your products": invite the question rather than dumping the list
        if (ChatLexicon.IsQuestionIntro(t.Norm)) { t.Say("ask_away"); return; }
        if (ChatLexicon.IsProducts(t.Norm)) { CatalogueReply(t, cat); return; }
        if (raws.Count == 0 || ChatLexicon.IsGreeting(t.Norm))
        {
            if (t.D.Lines.Count > 0 && ChatLexicon.IsYes(t.Norm)) { await AdvanceAsync(t, ct); return; }
            WelcomeReply(t, cat); return;
        }
        var near = parsed.FirstOrDefault(p => p.Near.Count > 0);
        if (near is not null && !(near.Raw.Qty is null && ChatLexicon.StartsWithGreeting(t.Norm))) { AskWhich(t, near.Near.Select(n => n.Id).ToList(), near.Raw.Qty); return; }
        if (ChatLexicon.StartsWithGreeting(t.Norm)) { WelcomeReply(t, cat); return; }      // "Hi Chewy Pet! I'd like some dog food" is a hello, not a product called "chewy pet"
        if (ChatLexicon.IsGeneric(t.Norm)) { CatalogueReply(t, cat); return; }              // "i wan buy dog food": show the range (no AI call needed)

        if (await TryAiAsync(t, cat, ct)) return;
        if (!ai.CanSpend(t.C) && t.C.AiCalls >= Opt.MaxAiCallsPerSale) { t.Say("handoff"); Track(t, ChatEventKinds.Handoff); }
        else t.Say("not_found", raws[0].Text);
    }

    /// <summary>Works through <see cref="Draft.Todo"/>. Returns false if it stopped to ask the customer something (the rest stays in Todo).</summary>
    private async Task<bool> ProcessAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        var todo = t.D.Todo; t.D.Todo = [];
        for (var i = 0; i < todo.Count; i++)
        {
            var p = ProductMatcher.Match(todo[i], cat);
            var rest = () => t.D.Todo = todo.Skip(i + 1).ToList();
            // the customer typed a product's full name ("DEKRA CAN"): that product, even if a shorter word also fits another one
            var named = ExactPick(t.Text, p.Matches.Count > 0 ? p.Matches : p.Near);
            if (named is not null && (p.Matches.Count > 1 || p.Matches.Count == 0)) { if (!AddLine(t, named, p.Raw.Qty)) { rest(); return false; } continue; }
            if (p.Matches.Count == 0)
            {
                if (p.Near.Count > 0) { AskWhich(t, p.Near.Select(n => n.Id).ToList(), p.Raw.Qty); rest(); return false; }
                t.Say("not_found", p.Raw.Text);
                continue;
            }
            if (p.Matches.Count > 1) { AskWhich(t, p.Matches.Take(4).Select(n => n.Id).ToList(), p.Raw.Qty); rest(); return false; }
            if (!AddLine(t, p.Matches[0], p.Raw.Qty)) { rest(); return false; }
        }
        await Task.CompletedTask;
        return true;
    }

    private bool AddLine(Turn t, CatalogItem item, int? qty)
    {
        if (item.Available <= 0) { t.Say("out_of_stock", item.Name); return true; }
        if (qty is null)
        {
            t.D.Waiting = new Pending { Kind = KQty, ProductIds = [item.Id] };
            t.Say("ask_qty", item.Name, ChatOrderText.Naira(item.Price));
            return false;
        }
        if (qty > item.Available)
        {
            t.D.Waiting = new Pending { Kind = KStock, ProductIds = [item.Id], Qty = qty, Available = item.Available };
            t.Say("only_n", item.Name, item.Available);
            return false;
        }
        var line = t.D.Lines.FirstOrDefault(l => l.ProductId == item.Id);
        if (line is null) t.D.Lines.Add(new DraftLine { ProductId = item.Id, Qty = qty.Value }); else line.Qty = qty.Value;   // the latest number the customer said wins
        if (t.D.Interest == item.Id) t.D.Interest = null;
        return true;
    }

    /// <summary>
    /// The one product whose every name word the customer typed (read from their own words, before filler such as "can" is removed). When several fit,
    /// the longest name wins ("dekra can" is DEKRA CAN, not DEKRA). Null when none or a tie.
    /// </summary>
    internal static CatalogItem? ExactPick(string text, IEnumerable<CatalogItem> candidates)
    {
        static string[] Words(string s) => System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0).ToArray();
        var said = Words(text).ToHashSet();
        var full = candidates.Select(c => (c, words: Words(c.Name))).Where(x => x.words.Length > 0 && x.words.All(said.Contains)).OrderByDescending(x => x.words.Length).ToList();
        return full.Count == 1 || (full.Count > 1 && full[0].words.Length > full[1].words.Length) ? full[0].c : null;
    }

    private void AskWhich(Turn t, List<int> ids, int? qty)
    {
        t.D.Waiting = new Pending { Kind = KWhich, ProductIds = ids, Qty = qty };
        var names = ids.Select(id => t.Cat!.First(c => c.Id == id).Name);
        t.Say("which", string.Join(" / ", names));
    }

    /// <summary>The customer answered a question we asked. Returns true when it is settled and the flow can move on.</summary>
    private async Task<bool> ResolveWaitingAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        var w = t.D.Waiting!;
        // in the order they were offered, so "2" means the second one listed
        var candidates = w.ProductIds.Select(id => cat.FirstOrDefault(c => c.Id == id)).OfType<CatalogItem>().ToList();
        if (candidates.Count == 0) { t.D.Waiting = null; return true; }
        var number = ProductMatcher.FirstQuantity(t.Norm);

        switch (w.Kind)
        {
            case KWhich:
            {
                if (int.TryParse(t.Norm.Trim(), out var nth) && nth >= 1 && nth <= candidates.Count) { t.D.Waiting = null; return AddLine(t, candidates[nth - 1], w.Qty); }
                if (ExactPick(t.Text, candidates) is { } exact) { t.D.Waiting = null; return AddLine(t, exact, w.Qty ?? ProductMatcher.FirstQuantity(t.Norm, ignoreWordOne: true)); }
                var line = ProductMatcher.Split(t.Norm).FirstOrDefault();
                var m = line is null ? null : ProductMatcher.Match(line, candidates);
                if (m is null || m.Matches.Count != 1)
                {
                    if (await TryAiAsync(t, cat, ct)) return false;
                    if (++w.Tries >= 2) { t.D.Waiting = null; CatalogueReply(t, cat); return false; }   // never ask the same question a third time
                    t.Say("which", string.Join(" / ", candidates.Select(c => c.Name))); return false;
                }
                t.D.Waiting = null;
                return AddLine(t, m.Matches[0], w.Qty ?? ProductMatcher.FirstQuantity(t.Norm, ignoreWordOne: true));
            }
            case KQty:
                if (number is null) { if (await TryAiAsync(t, cat, ct)) return false; t.Say("ask_qty", candidates[0].Name, ChatOrderText.Naira(candidates[0].Price)); return false; }
                t.D.Waiting = null;
                return AddLine(t, candidates[0], number);
            case KStock:
                if (ChatLexicon.IsNo(t.Norm)) { t.D.Waiting = null; return true; }
                if (ChatLexicon.IsYes(t.Norm)) { t.D.Waiting = null; return AddLine(t, candidates[0], w.Available); }
                if (number is not null) { t.D.Waiting = null; return AddLine(t, candidates[0], number); }
                if (await TryAiAsync(t, cat, ct)) return false;
                t.Say("only_n", candidates[0].Name, w.Available ?? 0); return false;
        }
        t.D.Waiting = null;
        return true;
    }

    /// <summary>All lines are settled: ask for a name if we need one, otherwise show the order and ask yes/no.</summary>
    private async Task AdvanceAsync(Turn t, CancellationToken ct)
    {
        if (t.D.Lines.Count == 0) return;
        if (PhoneOf(t) is null) { t.C.State = ChatStates.AwaitingPhone; t.Say("ask_phone"); return; }
        if (string.IsNullOrWhiteSpace(t.D.Name))
        {
            var phone = PhoneOf(t)!;
            var existing = await db.Customers.AsNoTracking().Where(c => c.Phone == phone).Select(c => c.Name).FirstOrDefaultAsync(ct);
            if (existing is not null && existing != phone) t.D.Name = existing;
            else if (UsableName(t.ProfileName)) t.D.Name = t.ProfileName.Trim();
        }
        if (string.IsNullOrWhiteSpace(t.D.Name)) { t.C.State = ChatStates.AwaitingName; t.Say("ask_name"); return; }
        await ProceedAsync(t, ct);
    }

    private async Task ShowSummaryAsync(Turn t, CancellationToken ct)
    {
        var cat = await CatAsync(t, ct);
        var vat = (await profile.GetAsync(ct)).DefaultVatRate;
        var rows = t.D.Lines.Select(l => (Item: cat.First(c => c.Id == l.ProductId), l.Qty)).ToList();
        var totals = DocumentCalculator.Sale(rows.Select(r => new SaleLineInput(r.Item.Id, r.Qty, r.Item.Price)), 0, vat);
        var fee = await DeliveryFeeAsync(t, ct);
        var lines = rows.Select(r => ChatOrderText.Line(r.Qty, r.Item.Name, r.Qty * r.Item.Price)).ToList();
        if (t.D.Zone is { } zone) lines.Add(fee > 0 ? $"• Delivery to {zone} — {ChatOrderText.Naira(fee)}" : $"• Delivery to {zone} — {ChatText.Get(t.Lang, "d_eta_default")}");
        t.C.State = ChatStates.AwaitingConfirm;
        Track(t, ChatEventKinds.Summary, totals.Total + fee);
        t.Say(t.D.Quote ? "quote_summary" : "summary", string.Join('\n', lines), ChatOrderText.Naira(totals.Total + fee));
    }

    // ------------------------------------------------------------------ who the customer is: their WhatsApp number

    /// <summary>On WhatsApp the sender's number IS the customer; on the web chat it is the number they typed (null until they do).</summary>
    private static string? PhoneOf(Turn t) =>
        t.C.Channel == ChatChannels.WhatsApp ? CustomerLookupService.Normalize(t.C.ExternalId) : t.D.Phone;

    /// <summary>Nigerian numbers in any usual form (08031234567, 8031234567, +234 803 123 4567, 2348031234567) become 2348031234567; other international numbers are kept as typed (10–15 digits).</summary>
    public static string? ParsePhone(string? text)
    {
        var raw = text ?? "";
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (d.StartsWith("234") && d.Length == 13) return d;
        if (d.StartsWith('0') && d.Length == 11) return "234" + d[1..];
        if (d.Length == 10 && d[0] is '7' or '8' or '9') return "234" + d;
        return raw.TrimStart().StartsWith('+') && d.Length is >= 10 and <= 15 ? d : null;
    }

    private async Task PhoneStepAsync(Turn t, CancellationToken ct)
    {
        var phone = ParsePhone(t.Text);
        if (phone is null) { t.Say("bad_phone"); return; }
        t.D.Phone = phone;
        await AdvanceAsync(t, ct);
    }

    // ------------------------------------------------------------------ marketing consent (opt-in) and STOP / SUBSCRIBE

    /// <summary>After the order is understood and we know the name: if this customer has never answered, ask once. Only then the summary.</summary>
    private async Task ProceedAsync(Turn t, CancellationToken ct)
    {
        if (t.D.Consent is null && await consent.StatusByPhoneAsync(PhoneOf(t) ?? "", ct) == MarketingStatuses.Unknown)
        {
            t.C.State = ChatStates.AwaitingConsent;
            t.Say("consent_ask", company.LegalName);
            return;
        }
        await ShowSummaryAsync(t, ct);
    }

    private async Task ConsentStepAsync(Turn t, CancellationToken ct)
    {
        if (ChatLexicon.IsYes(t.Norm)) t.D.Consent = "in";
        else if (ChatLexicon.IsNo(t.Norm)) t.D.Consent = "out";
        else { t.Say("consent_ask", company.LegalName); return; }
        await ApplyConsentToExistingCustomerAsync(t, ct);
        t.Say(t.D.Consent == "in" ? "consent_yes" : "consent_no");
        await ShowSummaryAsync(t, ct);
    }

    private async Task ApplyConsentToExistingCustomerAsync(Turn t, CancellationToken ct)
    {
        var phone = PhoneOf(t);
        if (phone is null) return;                                      // web chat before the number is known: applied when the order is placed
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone, ct);
        if (existing is not null && t.D.Consent is "in" or "out")
            await consent.SetAsync(existing, t.D.Consent == "in" ? MarketingStatuses.OptedIn : MarketingStatuses.OptedOut, "chat", t.Lang, ct);
    }

    /// <summary>STOP / UNSUBSCRIBE / SUBSCRIBE at any point. A bare STOP in the middle of an order still means "cancel the order"; the longer phrases always mean "no more messages".</summary>
    private async Task<bool> HandleMarketingCommandAsync(Turn t, CancellationToken ct)
    {
        var n = t.Norm;
        var bare = ChatLexicon.IsBareStop(n);
        var idleish = t.C.State is ChatStates.Idle or ChatStates.Collecting && t.D.Lines.Count == 0 && t.D.Waiting is null;
        var optOut = ChatLexicon.IsOptOut(n) || (bare && (idleish || t.C.State == ChatStates.AwaitingConsent));
        var optIn = ChatLexicon.IsOptIn(n);
        if (!optOut && !optIn) return false;

        if (t.C.State == ChatStates.AwaitingConsent)
        {
            if (!optOut) return false;                              // "subscribe" while being asked is just a yes
            t.D.Consent = "out";
            await ApplyConsentToExistingCustomerAsync(t, ct);
            t.Say("consent_no");
            await ShowSummaryAsync(t, ct);
            return true;
        }
        var phone = PhoneOf(t);
        if (phone is not null)
        {
            var customer = await customers.FindOrCreateAsync(phone, t.D.Name, ct);
            await consent.SetAsync(customer, optOut ? MarketingStatuses.OptedOut : MarketingStatuses.OptedIn, "chat", t.Lang, ct);
        }
        t.D.Consent = optOut ? "out" : "in";
        t.Say(optOut ? "optout_done" : "optin_done");
        return true;
    }

    // ------------------------------------------------------------------ a campaign message opens a conversation: what "YES" will mean

    /// <summary>Conversation state for "Reply YES and I'll prepare the same order again": the lines are waiting on the summary/yes step.</summary>
    public static (string State, string DraftJson) PrimedOrder(IEnumerable<(int ProductId, int Qty)> lines, string? name) =>
        (ChatStates.AwaitingConfirm, JsonSerializer.Serialize(new Draft { Lines = lines.Select(l => new DraftLine { ProductId = l.ProductId, Qty = l.Qty }).ToList(), Name = name, Consent = "in" }, Json));

    /// <summary>Conversation state for an offer: the next number the customer sends is the quantity of this product.</summary>
    public static (string State, string DraftJson) PrimedAskQuantity(int productId, string? name) =>
        (ChatStates.Collecting, JsonSerializer.Serialize(new Draft { Name = name, Consent = "in", Waiting = new Pending { Kind = KQty, ProductIds = [productId] } }, Json));

    // ------------------------------------------------------------------ name

    private async Task NameStepAsync(Turn t, CancellationToken ct)
    {
        var name = ExtractName(t.Text);
        if (name is null || ChatLexicon.IsYes(t.Norm) || ChatLexicon.IsNo(t.Norm) || ChatLexicon.IsGreeting(t.Norm) || ChatLexicon.IsProducts(t.Norm)) { t.Say("ask_name"); return; }
        t.D.Name = name;
        await ProceedAsync(t, ct);
    }

    private static readonly Regex NamePrefix = new(@"^\s*(my name is|my name na|my name|i am|i'm|im|i be|this is|it is|its|name|sunana|suna na|suna|aha m bu|aham bu|aha m|a na m akpo m)\b[:\s,-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NameShape = new(@"^\p{L}[\p{L}\p{M} .'\-]{1,59}$", RegexOptions.CultureInvariant);

    private static string? ExtractName(string text)
    {
        var n = NamePrefix.Replace(text ?? "", "").Trim().Trim('.', ',', '!');
        if (n.Length < 2 || n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5 || !NameShape.IsMatch(n)) return null;
        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static bool UsableName(string? n) => ExtractName(n ?? "") is not null;

    // ------------------------------------------------------------------ confirm → order → payment account

    private async Task ConfirmStepAsync(Turn t, CancellationToken ct)
    {
        var cat = await CatAsync(t, ct);
        if (ChatLexicon.ExtractEmail(t.Text) is { } typedEmail) { t.D.Email = typedEmail; if (t.D.Quote) { await PlaceQuoteAsync(t, cat, ct); return; } }
        if (t.D.Quote)
        {
            if (ChatLexicon.IsPay(t.Norm)) { t.D.Quote = false; await ShowSummaryAsync(t, ct); return; }
            if (ChatLexicon.IsSend(t.Norm)) { await PlaceQuoteAsync(t, cat, ct); return; }
        }
        if (ChatLexicon.IsYes(t.Norm)) { await PlaceOrderAsync(t, cat, ct); return; }
        if (ChatLexicon.IsNo(t.Norm)) { t.D.Lines.Clear(); t.C.State = ChatStates.Collecting; t.Say("order_changed"); return; }

        var parsed = ProductMatcher.Split(t.Norm).Select(r => ProductMatcher.Match(r, cat)).ToList();
        if (parsed.Any(p => p.Matches.Count > 0)) { await CollectAsync(t, ct); return; }      // "make it 3" / "add beans": change the order, then show it again
        if (ChatLexicon.IsProducts(t.Norm)) { CatalogueReply(t, cat); return; }
        if (await TryAiAsync(t, cat, ct)) return;
        await ShowSummaryAsync(t, ct);
    }

    private async Task PlaceOrderAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        // Re-check stock against the database right now: the summary may be minutes old.
        foreach (var l in t.D.Lines.ToList())
        {
            var item = cat.FirstOrDefault(c => c.Id == l.ProductId);
            if (item is null || item.Available <= 0) { t.D.Lines.Remove(l); t.Say("out_of_stock", item?.Name ?? "?"); }
            else if (item.Available < l.Qty)
            {
                t.D.Lines.Remove(l);
                t.D.Waiting = new Pending { Kind = KStock, ProductIds = [item.Id], Qty = l.Qty, Available = item.Available };
                t.Say("only_n", item.Name, item.Available);
                t.C.State = ChatStates.Collecting; return;
            }
        }
        if (t.D.Lines.Count == 0) { t.C.State = ChatStates.Collecting; return; }

        var phone = PhoneOf(t);
        if (phone is null) { t.C.State = ChatStates.AwaitingPhone; t.Say("ask_phone"); return; }
        var customer = await customers.FindOrCreateAsync(phone, t.D.Name, ct);
        if (!string.IsNullOrWhiteSpace(t.D.Name) && customer.Name == phone) { customer.Name = t.D.Name!; await db.SaveChangesAsync(ct); }
        if (t.D.Consent is "in" or "out" && customer.MarketingStatus == MarketingStatuses.Unknown)
            await consent.SetAsync(customer, t.D.Consent == "in" ? MarketingStatuses.OptedIn : MarketingStatuses.OptedOut, "chat", t.Lang, ct);
        else if (customer.PreferredLanguage != t.Lang) { customer.PreferredLanguage = t.Lang; await db.SaveChangesAsync(ct); }

        // A previous, never-paid quotation from this chat is replaced rather than left lying around.
        if (t.C.QuotationId is int old && !await db.PaymentLinks.AnyAsync(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == old, ct))
        {
            try { await quotations.DeleteAsync(old, SalesBot, ct); } catch (BusinessRuleException) { /* already converted or gone (NotFoundException is one too): nothing to clean */ }
        }

        var vat = (await profile.GetAsync(ct)).DefaultVatRate;
        QuoteResult quote;
        try
        {
            quote = await quotations.CreateAsync(new QuoteRequest
            {
                CustomerId = customer.Id, PriceTier = await TierAsync(t, ct), DiscountPct = 0, VatRate = vat,
                DeliveryFee = await DeliveryFeeAsync(t, ct), DeliveryZone = t.D.Zone, DeliveryAddress = t.D.Address,
                // The price is always read from the product record here, never from anything the customer or the AI said.
                Lines = t.D.Lines.Select(l => new SaleLineDto { ProductId = l.ProductId, Quantity = l.Qty, UnitPrice = cat.First(c => c.Id == l.ProductId).Price }).ToList(),
            }, SalesBot, ct);
        }
        catch (BusinessRuleException) { t.Say("error"); return; }
        t.C.QuotationId = quote.Id; t.C.CustomerId = customer.Id;

        PayAccount? account;
        try { account = await pay.GetOrCreateVirtualAccountAsync(quote.Id, quote.Number, quote.Total, customer.Name, phone, customer.Email, ct); }
        catch (BusinessRuleException) { t.Say("error"); return; }     // stay on the summary: the customer can say yes again to retry
        // A secure card / USSD / bank-transfer page as well, when the card processor is set up: one tap instead of typing an account number.
        PayLink? card = null;
        if (pay.Enabled)
        {
            // At most a few seconds: WhatsApp gives up on the whole reply after 15, and the bank account above is already enough to pay.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try { card = await pay.GetOrCreateAsync(PaymentDocTypes.Quotation, quote.Id, quote.Number, quote.Total, customer.Email, customer.Name, limit.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { log.LogWarning(ex, "Could not make a payment link for {Number}", quote.Number); }
        }
        if (account is null && card is null) { t.C.State = ChatStates.Idle; t.Say("pay_unavailable"); return; }

        t.C.State = ChatStates.AwaitingPayment;
        Track(t, ChatEventKinds.Order, quote.Total, quote.Number);
        if (account is not null) t.Out.Add(ChatOrderText.PayInstructions(t.Lang, account));
        if (card is not null) t.Out.Add(ChatText.Get(t.Lang, account is null ? "pay_link_only" : "pay_link_also", ChatOrderText.Naira(quote.Total), card.Url));
    }

    // ------------------------------------------------------------------ waiting for the transfer

    private async Task PaymentStepAsync(Turn t, CancellationToken ct)
    {
        var quotationId = t.C.QuotationId;
        var links = quotationId is null ? [] : await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == quotationId).OrderByDescending(l => l.Id).ToListAsync(ct);
        // The bank account is the primary link (what the customer was first shown); a card / USSD link, if there is one, is the other way to pay the same order.
        var link = links.FirstOrDefault(l => l.ExternalId != null) ?? links.FirstOrDefault();
        if (link is null) { t.C.State = ChatStates.Idle; await CollectAsync(t, ct); return; }

        var finished = links.FirstOrDefault(l => l.Status != PaymentLinkStatuses.Pending);
        if (finished is not null) { await SayFinalAsync(t, finished, null, ct); return; }
        if (ChatLexicon.ExtractEmail(t.Text) is { } mail && quotationId is int qid)
        {
            var number = await db.Quotations.AsNoTracking().Where(x => x.Id == qid).Select(x => x.QuotationNumber).FirstOrDefaultAsync(ct) ?? "";
            await EmailQuoteAsync(t, qid, number, mail, ct);
            return;
        }

        if (ChatLexicon.IsPaidClaim(t.Norm) || ChatLexicon.IsGreeting(t.Norm))
        {
            foreach (var l in links)                      // they may have paid by either route: ask each processor
            {
                var r = await pay.SettleAsync(l.Reference, ct);
                if (r.Applied || r.Unfulfilled) { await SayFinalAsync(t, l, r, ct); return; }
                if (r.Shortfall is decimal) { t.Out.Add(ChatOrderText.Underpaid(t.Lang, r.Paid ?? 0, l.Amount, l.BankName ?? "", l.AccountNumber ?? "")); return; }
                if (r.AlreadySettled)
                {
                    var fresh = await db.PaymentLinks.AsNoTracking().SingleAsync(x => x.Id == l.Id, ct);
                    if (fresh.Status != PaymentLinkStatuses.Pending) { await SayFinalAsync(t, fresh, null, ct); return; }
                }
            }
            if (link.ExpiresAt < clock.UtcNow) { ResetSale(t); t.Say("expired"); return; }
            SayStillWaiting(t, link, links);
            return;
        }

        var cat = await CatAsync(t, ct);
        if (ProductMatcher.Split(t.Norm).Select(r => ProductMatcher.Match(r, cat)).Any(p => p.Matches.Count > 0)) { t.Say("stale_pay", ChatOrderText.Naira(link.Amount)); return; }
        if (ChatLexicon.IsProducts(t.Norm)) { CatalogueReply(t, cat); return; }
        if (link.ExpiresAt < clock.UtcNow) { ResetSale(t); t.Say("expired"); return; }
        if (await TryAiAsync(t, cat, ct)) return;
        SayStillWaiting(t, link, links);
    }

    private static void SayStillWaiting(Turn t, PaymentLink primary, IReadOnlyList<PaymentLink> all)
    {
        if (primary.ExternalId != null) t.Say("pay_wait", ChatOrderText.Naira(primary.Amount), primary.BankName ?? "", primary.AccountNumber ?? "");
        else t.Say("pay_wait_link", ChatOrderText.Naira(primary.Amount), primary.Url);
    }

    private async Task SayFinalAsync(Turn t, PaymentLink link, SettleResult? r, CancellationToken ct)
    {
        if (link.Status == PaymentLinkStatuses.PaidUnfulfilled || r?.Unfulfilled == true)
            t.Out.Add(await texts.UnfulfilledAsync(t.Lang, link.DocId, r?.Paid ?? link.PaidAmount ?? link.Amount, ct));
        else
            t.Out.Add(await texts.PaidAsync(t.Lang, link.DocId, r?.Paid ?? link.PaidAmount ?? link.Amount, r?.InvoiceNumber, ct));
        ResetSale(t);
    }

    // ------------------------------------------------------------------ delivery information and quotations (anyone can ask)

    /// <summary>"Where is my order?" / "waybill": answered from the customer's own recent sales and waybills, matched by the number they are writing from.</summary>
    private async Task<bool> HandleWaybillAsync(Turn t, CancellationToken ct)
    {
        if (!ChatLexicon.IsWaybill(t.Norm) || t.C.State is ChatStates.AwaitingPayment or ChatStates.AwaitingName or ChatStates.AwaitingConsent or ChatStates.AwaitingPhone) return false;
        var cat = await CatAsync(t, ct);
        if (ProductMatcher.Split(t.Norm).Select(r => ProductMatcher.Match(r, cat)).Any(p => p.Matches.Count > 0)) return false;     // "2 rice with delivery" is an order, not a question
        Track(t, ChatEventKinds.Waybill);
        var phone = PhoneOf(t);
        var customerId = phone is null ? null : await db.Customers.AsNoTracking().Where(c => c.Phone == phone).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        var invoices = customerId is null ? [] : await db.Invoices.AsNoTracking().Where(i => i.CustomerId == customerId && i.VoidedAt == null && !i.IsSample)
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id).Take(3).Select(i => new { i.Id, i.InvoiceNumber, i.InvoiceDate }).ToListAsync(ct);
        if (invoices.Count == 0) { t.Say("waybill_none"); return true; }
        var ids = invoices.Select(i => i.Id).ToList();
        var ways = await db.Waybills.AsNoTracking().Where(w => ids.Contains(w.InvoiceId)).OrderByDescending(w => w.Id).ToListAsync(ct);
        var lines = invoices.Select(i =>
        {
            var w = ways.FirstOrDefault(x => x.InvoiceId == i.Id);
            var day = i.InvoiceDate.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
            if (w is null) return ChatText.Get(t.Lang, "waybill_without", i.InvoiceNumber, day);
            var driver = string.IsNullOrWhiteSpace(w.DriverName) ? "" : ChatText.Get(t.Lang, "waybill_driver", (w.DriverName + " " + w.DriverPhone).Trim());
            return ChatText.Get(t.Lang, "waybill_with", i.InvoiceNumber, day, w.WaybillNumber, w.IssueDate.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), driver);
        });
        t.Out.Add(ChatText.Get(t.Lang, "waybill_head") + "\n" + string.Join('\n', lines));
        return true;
    }

    /// <summary>"Quote" / "quotation": the same item collecting as an order, but it ends in a quotation (on WhatsApp, and by email when we have an address) instead of a payment account.</summary>
    private async Task<bool> HandleQuoteIntentAsync(Turn t, CancellationToken ct)
    {
        if (!ChatLexicon.IsQuote(t.Norm) || t.C.State is ChatStates.AwaitingName or ChatStates.AwaitingConsent or ChatStates.AwaitingPhone) return false;
        var cat = await CatAsync(t, ct);
        if (t.C.State == ChatStates.AwaitingPayment)
        {
            if (ProductMatcher.Split(t.Norm).Select(r => ProductMatcher.Match(r, cat)).Any(p => p.Matches.Count > 0 || p.Near.Count > 0))
            {   // "quote 10 puppy" while an account is open: start a fresh quotation; the open order keeps its quotation so a transfer on its way is still matched
                var open = t.C.QuotationId;
                ResetSale(t);
                t.C.QuotationId = open;
                t.D.Quote = true;
                if (ChatLexicon.ExtractEmail(t.Text) is { } m) t.D.Email = m;
                return false;
            }
            await QuoteOpenOrderAsync(t, cat, ct);     // "i want quotation": the order they are about to pay for
            return true;
        }
        t.D.Quote = true;
        if (ChatLexicon.ExtractEmail(t.Text) is { } mail) t.D.Email = mail;
        var parsed = ProductMatcher.Split(t.Norm).Select(r => ProductMatcher.Match(r, cat)).ToList();
        if (parsed.Any(p => p.Matches.Count > 0 || p.Near.Count > 0)) return false;       // "quote 50 adult dog food": the normal flow collects the lines, then the summary offers the quotation
        if (t.D.Lines.Count > 0) { await ShowSummaryAsync(t, ct); return true; }          // quote the order already being built
        t.C.State = ChatStates.Collecting;
        t.Say("quote_start", ExampleProduct(t));
        return true;
    }

    /// <summary>
    /// One new quotation per number (or website visitor) per rolling day, set by <see cref="SalesAssistantOptions.QuotesPerDay"/>. A second request
    /// gets today's quotation number back, a suggestion to check again in an hour, and the offer to buy those items now: no new document, no email.
    /// </summary>
    private async Task<bool> QuoteLimitReachedAsync(Turn t, int customerId, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-1);
        var today = await db.ChatEvents.AsNoTracking()
            .Where(e => e.Kind == ChatEventKinds.Quote && e.At > since && (e.ConversationId == t.C.Id || e.CustomerId == customerId))
            .OrderByDescending(e => e.Id).Select(e => new { e.Reference, e.Amount }).ToListAsync(ct);
        if (today.Count < Math.Max(1, Opt.QuotesPerDay)) return false;
        Track(t, ChatEventKinds.QuoteLimited);
        t.Say("quote_limit", today[0].Reference ?? "", ChatOrderText.Naira(today[0].Amount ?? 0));
        t.D.Quote = false;                       // "yes" now buys the items they asked about
        t.C.State = ChatStates.AwaitingConfirm;
        return true;
    }

    /// <summary>The quotation behind the order awaiting payment: its lines and total, emailed as a PDF when the message carries an address.</summary>
    private async Task QuoteOpenOrderAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        var q = t.C.QuotationId is int id ? await db.Quotations.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.QuotationNumber, x.TotalAmount }).FirstOrDefaultAsync(ct) : null;
        if (q is null) { t.C.State = ChatStates.Collecting; t.D.Quote = true; t.Say("quote_start", ExampleProduct(t)); return; }
        Track(t, ChatEventKinds.Quote, q.TotalAmount, q.QuotationNumber);
        t.Say("quote_current", q.QuotationNumber, await texts.ReceiptLinesAsync(q.Id, ct), ChatOrderText.Naira(q.TotalAmount), ExampleProduct(t));
        if (ChatLexicon.ExtractEmail(t.Text) is { } to) await EmailQuoteAsync(t, q.Id, q.QuotationNumber, to, ct);
    }

    private async Task EmailQuoteAsync(Turn t, int quotationId, string number, string to, CancellationToken ct)
    {
        if (emails is null) { t.Say("quote_email_failed"); return; }
        try { await emails.SendQuotationAsync(quotationId, to, null, SalesBot, ct); t.Say("quote_emailed", to); }
        catch (Exception ex) when (ex is BusinessRuleException or System.Net.Mail.SmtpException or IOException) { log.LogWarning(ex, "Could not email quotation {Number}", number); t.Say("quote_email_failed"); }
    }

    private async Task PlaceQuoteAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        t.D.Lines.RemoveAll(l => cat.All(c => c.Id != l.ProductId));
        if (t.D.Lines.Count == 0) { t.C.State = ChatStates.Collecting; return; }
        var phone = PhoneOf(t);
        if (phone is null) { t.C.State = ChatStates.AwaitingPhone; t.Say("ask_phone"); return; }
        var customer = await customers.FindOrCreateAsync(phone, t.D.Name, ct);
        if (!string.IsNullOrWhiteSpace(t.D.Name) && customer.Name == phone) customer.Name = t.D.Name!;
        if (!string.IsNullOrWhiteSpace(t.D.Email) && string.IsNullOrWhiteSpace(customer.Email)) customer.Email = t.D.Email;
        await db.SaveChangesAsync(ct);
        if (await QuoteLimitReachedAsync(t, customer.Id, ct)) return;

        var vat = (await profile.GetAsync(ct)).DefaultVatRate;
        QuoteResult quote;
        try
        {
            quote = await quotations.CreateAsync(new QuoteRequest
            {
                CustomerId = customer.Id, PriceTier = await TierAsync(t, ct), DiscountPct = 0, VatRate = vat,
                Lines = t.D.Lines.Select(l => new SaleLineDto { ProductId = l.ProductId, Quantity = l.Qty, UnitPrice = cat.First(c => c.Id == l.ProductId).Price }).ToList(),
            }, SalesBot, ct);
        }
        catch (BusinessRuleException) { t.Say("error"); return; }
        t.C.CustomerId = customer.Id;
        Track(t, ChatEventKinds.Quote, quote.Total, quote.Number);
        var rows = t.D.Lines.Select(l => (Item: cat.First(c => c.Id == l.ProductId), l.Qty)).ToList();
        t.Say("quote_ready", quote.Number, string.Join('\n', rows.Select(r => ChatOrderText.Line(r.Qty, r.Item.Name, r.Qty * r.Item.Price))), ChatOrderText.Naira(quote.Total));

        var to = t.D.Email ?? customer.Email;
        if (!string.IsNullOrWhiteSpace(to))
        {
            await EmailQuoteAsync(t, quote.Id, quote.Number, to, ct);
        }
        t.D.Quote = false;                       // back to an ordinary order: "yes" now buys these same items
        t.C.State = ChatStates.AwaitingConfirm;
    }
    // ------------------------------------------------------------------ replies

    private void WelcomeReply(Turn t, List<CatalogItem> cat) => t.Out.Add(ChatText.Get(t.Lang, "welcome", company.LegalName, cat.FirstOrDefault(p => p.Available > 0)?.Name.ToLowerInvariant() ?? "rice"));

    private void CatalogueReply(Turn t, List<CatalogItem> cat)
    {
        var inStock = cat.Where(p => p.Available > 0).Take(15).ToList();
        if (inStock.Count == 0) { t.Say("catalogue_empty"); return; }
        Track(t, ChatEventKinds.Browsed);
        t.Out.Add(ChatText.Get(t.Lang, "catalogue_head") + "\n" + string.Join('\n', inStock.Select(p => $"• {p.Name} — {ChatOrderText.Naira(p.Price)}")) + "\n\n" + ChatText.Get(t.Lang, "catalogue_tail", inStock[0].Name.ToLowerInvariant()));
    }

    // ------------------------------------------------------------------ AI (last resort)

    /// <summary>Returns true when the AI's reading was acted on (a reply was produced). Costs one credit; refused past the per-sale ceiling and after one try per message.</summary>
    private async Task<bool> TryAiAsync(Turn t, List<CatalogItem> cat, CancellationToken ct)
    {
        if (t.AiTried || !ai.CanSpend(t.C)) return false;
        t.AiTried = true;
        var r = await ai.InterpretAsync(t.C, t.Text, cat, t.C.State, ct);
        if (r is null) return false;
        // The AI may only help where the words could not: if we still think "English" (the default) and it recognises another language (Igala, say), take its word.
        // It never overrides a language the words already identified, and never switches a conversation back to English (a live test showed it doing exactly that to Pidgin).
        if (r.Language is { } aiLang && aiLang != ChatLanguages.English && t.C.Language == ChatLanguages.English) t.C.Language = aiLang;

        switch (r.Intent)
        {
            case "order" when r.Lines.Count > 0:
                if (t.C.State == ChatStates.AwaitingPayment)
                {
                    var owed = await db.PaymentLinks.AsNoTracking().Where(l => l.DocType == PaymentDocTypes.Quotation && l.DocId == t.C.QuotationId)
                        .OrderByDescending(l => l.Id).Select(l => l.Amount).FirstOrDefaultAsync(ct);
                    t.Say("stale_pay", ChatOrderText.Naira(owed)); return true;
                }
                if (t.C.State == ChatStates.AwaitingConfirm) t.C.State = ChatStates.Collecting;
                t.D.Waiting = null;
                t.D.Todo = r.Lines.ToList();
                if (await ProcessAsync(t, cat, ct)) await AdvanceAsync(t, ct);
                return true;
            case "catalogue": CatalogueReply(t, cat); return true;
            case ChatLexicon.AskDelivery or ChatLexicon.AskPrice or ChatLexicon.AskStock or ChatLexicon.AskInfo or ChatLexicon.AskRecommend
                or ChatLexicon.AskHours or ChatLexicon.AskLocation or ChatLexicon.AskPayHow or ChatLexicon.AskDiscount or ChatLexicon.AskHuman:
                return await HandleEnquiryAsync(t, ct, r.Intent, r.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToList());
            case "yes": t.Norm = "yes"; break;
            case "no": t.Norm = "no"; break;
            case "cancel": t.Norm = "cancel"; break;
            case "payment_done": t.Norm = "paid"; break;
            case "greeting": t.Norm = "hello"; break;
            default: return false;
        }
        await StepAsync(t, ct);     // re-dispatch with the canonical word; AiTried stops it from spending again
        return true;
    }
}
