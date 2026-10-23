using System.Globalization;
using System.Text.RegularExpressions;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Partners;

public sealed record PartnerApplication(string BusinessName, string ContactName, string Phone, string Email, string Address, string CacNumber, string? TaxId,
    string Description, string RequestedTier);
public sealed record PartnerUpload(string Kind, string FileName, string ContentType, byte[] Data);
public sealed record PartnerApplied(int Id, string Status, string Message);

public sealed record PartnerDocDto(int Id, string Kind, string FileName, string ContentType, int Size, DateTime UploadedAt);
public sealed record TradePartnerDto(int Id, string BusinessName, string ContactName, string Phone, string Email, string Address, string CacNumber, string? TaxId,
    string Description, string RequestedTier, string? ApprovedTier, string Status, string Channel, string Verification, string? VerifiedName, string? VerificationNote,
    int? CustomerId, DateTime CreatedAt, DateTime? DecidedAt, string? DecidedBy, string? DecisionNote, decimal WalletBalance, IReadOnlyList<PartnerDocDto> Documents);

/// <summary>
/// Pet shops, wholesalers and distributors applying for trade prices, and staff deciding on them. Nobody gets trade prices by filling a form:
/// an application sits as Pending (the chat keeps quoting retail) until a manager approves it and chooses the tier.
/// </summary>
public sealed partial class TradePartnerService(IBusinessDbContext db, IClock clock, ICompanyContext company, IBusinessVerifier verifier, IStaffAlerts alerts,
    ILogger<TradePartnerService> log)
{
    public const int MaxDocumentBytes = 3_000_000;
    private static readonly string[] AllowedTypes = ["application/pdf", "image/jpeg", "image/png", "image/webp"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^(RC|BN|IT|LP|LLP)?\s*-?\s*\d{3,9}$", RegexOptions.IgnoreCase)] private static partial Regex CacPattern();
    [GeneratedRegex(@"^\d{8}-?\d{4}$|^\d{10,14}$")] private static partial Regex TinPattern();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")] private static partial Regex EmailPattern();

    // ------------------------------------------------------------------ applying (website or WhatsApp)

    public async Task<PartnerApplied> ApplyAsync(PartnerApplication a, IReadOnlyList<PartnerUpload> uploads, string channel, CancellationToken ct)
    {
        var app = Clean(a);
        foreach (var u in uploads) CheckUpload(u);
        if (channel == "website" && !uploads.Any(u => u.Kind == "cac"))
            throw new BusinessRuleException("Please attach your CAC certificate (PDF or photo).");

        var existing = await db.TradePartners.Include(p => p.Documents).FirstOrDefaultAsync(p => p.Phone == app.Phone, ct);
        if (existing is { Status: PartnerStatuses.Approved })
            return new(existing.Id, existing.Status, $"{existing.BusinessName} is already approved as a {existing.ApprovedTier?.ToLowerInvariant()}. Your trade prices show whenever you chat or shop from {Pretty(app.Phone)}.");
        if (existing is { Status: PartnerStatuses.Suspended })
            throw new BusinessRuleException("This number's trade account is on hold. Please call us so we can sort it out.");

        var p = existing ?? new TradePartner { Phone = app.Phone, CreatedAt = clock.UtcNow };
        p.BusinessName = app.BusinessName; p.ContactName = app.ContactName; p.Email = app.Email; p.Address = app.Address;
        p.CacNumber = app.CacNumber; p.TaxId = app.TaxId; p.Description = app.Description; p.RequestedTier = app.RequestedTier;
        p.Status = PartnerStatuses.Pending; p.Channel = channel; p.DecidedAt = null; p.DecidedBy = null; p.DecisionNote = null;
        foreach (var u in uploads)
        {
            // one file per kind: a new CAC certificate replaces the old one
            p.Documents.RemoveAll(d => d.Kind == u.Kind);
            p.Documents.Add(new TradePartnerDocument { Kind = u.Kind, FileName = SafeName(u.FileName), ContentType = u.ContentType, Data = u.Data, UploadedAt = clock.UtcNow });
        }

        var check = await VerifySafelyAsync(p.CacNumber, p.BusinessName, ct);
        p.Verification = check.Status; p.VerifiedName = check.RegisteredName; p.VerificationNote = check.Note;
        if (existing is null) db.TradePartners.Add(p);
        await db.SaveChangesAsync(ct);

        await alerts.TellStaffAsync(NewApplicationAlert(p, existing is not null), ct);
        await alerts.TellContactAsync(new ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: we have your trade application",
            $"Hello {First(p.ContactName)}, thank you for applying to buy from {company.LegalName} at {p.RequestedTier.ToLowerInvariant()} prices. " +
            "We are checking your details now and will message you as soon as a manager has decided (usually within one working day). Until then you'll see our retail prices."), ct);
        return new(p.Id, p.Status, $"Thank you, {First(p.ContactName)}. Your application is with our managers. We'll message {Pretty(p.Phone)} and {p.Email} once it's decided.");
    }

    /// <summary>The applicant's own view of where they stand, by phone number (used by the chat and the website).</summary>
    public async Task<TradePartner?> ByPhoneAsync(string phone, CancellationToken ct)
    {
        var digits = ChatFlowService.ParsePhone(phone);
        return digits is null ? null : await db.TradePartners.AsNoTracking().FirstOrDefaultAsync(p => p.Phone == digits, ct);
    }

    // ------------------------------------------------------------------ staff

    public async Task<List<TradePartnerDto>> ListAsync(string? status, CancellationToken ct)
    {
        var list = await db.TradePartners.AsNoTracking().Where(p => status == null || p.Status == status).OrderByDescending(p => p.Id).Take(1000)
            .Select(p => new { p, Docs = p.Documents.Select(d => new PartnerDocDto(d.Id, d.Kind, d.FileName, d.ContentType, d.Data.Length, d.UploadedAt)).ToList() })
            .ToListAsync(ct);
        var ids = list.Select(x => x.p.Id).ToList();
        var balances = await db.WalletEntries.AsNoTracking().Where(w => ids.Contains(w.TradePartnerId) && w.Status == WalletEntryStatuses.Confirmed)
            .GroupBy(w => w.TradePartnerId).Select(g => new { g.Key, Sum = g.Sum(w => w.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return list.Select(x => Dto(x.p, x.Docs, balances.GetValueOrDefault(x.p.Id))).ToList();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> DocumentAsync(int partnerId, int docId, CancellationToken ct) =>
        await db.TradePartnerDocuments.AsNoTracking().Where(d => d.Id == docId && d.TradePartnerId == partnerId).Select(d => new { d.FileName, d.ContentType, d.Data }).FirstOrDefaultAsync(ct) is { } d
            ? (d.FileName, d.ContentType, d.Data) : null;

    /// <summary>Approves at the tier staff choose and books the partner to a customer record (the one with their number, or a new one) at that tier.</summary>
    public async Task ApproveAsync(int id, string tier, string? note, CurrentUser user, CancellationToken ct)
    {
        if (tier is not (PriceTiers.Wholesaler or PriceTiers.Distributor)) throw new BusinessRuleException("Approve as Wholesaler or Distributor.");
        var p = await db.TradePartners.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Trade partner");
        if (p.Status == PartnerStatuses.Approved && p.ApprovedTier == tier) return;

        var customer = p.CustomerId is int cid ? await db.Customers.FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        customer ??= await db.Customers.FirstOrDefaultAsync(c => c.Phone == p.Phone, ct);
        if (customer is null) { customer = new Customer { Name = p.BusinessName, Phone = p.Phone, RebatePerUnit = 0 }; db.Customers.Add(customer); }
        customer.CustomerType = tier; customer.ContactName ??= p.ContactName; customer.Email ??= p.Email; customer.Address ??= p.Address; customer.TaxId ??= p.TaxId;
        if (customer.Name == customer.Phone) customer.Name = p.BusinessName;

        p.Customer = customer; p.ApprovedTier = tier; p.Status = PartnerStatuses.Approved;
        p.DecidedAt = clock.UtcNow; p.DecidedBy = user.FullName; p.DecisionNote = Trim(note, 300);
        Audit(user, "PARTNER_APPROVED", p, $"{p.BusinessName} as {tier}");
        await db.SaveChangesAsync(ct);

        await alerts.TellContactAsync(new ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: you're approved for {tier.ToLowerInvariant()} prices",
            $"Good news, {First(p.ContactName)}! {p.BusinessName} is approved to buy from {company.LegalName} at {tier.ToLowerInvariant()} prices. " +
            $"Chat with us on WhatsApp from {Pretty(p.Phone)} or sign in to the partner portal and you'll see your prices straight away."), ct);
        await alerts.TellStaffAsync(new StaffAlert($"{company.LegalName}: {p.BusinessName} approved as {tier}", $"{user.FullName} approved {p.BusinessName} ({Pretty(p.Phone)}) as {tier}."), ct);
    }

    public async Task RejectAsync(int id, string? note, CurrentUser user, CancellationToken ct)
    {
        var p = await db.TradePartners.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Trade partner");
        if (p.Status == PartnerStatuses.Approved) throw new BusinessRuleException("This partner is already approved. Suspend them instead.");
        p.Status = PartnerStatuses.Rejected; p.DecidedAt = clock.UtcNow; p.DecidedBy = user.FullName; p.DecisionNote = Trim(note, 300);
        Audit(user, "PARTNER_REJECTED", p, note);
        await db.SaveChangesAsync(ct);
        await alerts.TellContactAsync(new ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: your trade application",
            $"Hello {First(p.ContactName)}, we couldn't approve {p.BusinessName} for trade prices this time" + (string.IsNullOrWhiteSpace(p.DecisionNote) ? "." : $": {p.DecisionNote}.") +
            " You're welcome to apply again with updated details, and you can still buy at our retail prices."), ct);
    }

    /// <summary>Takes trade prices and portal access away at once; history and wallet stay. <paramref name="suspend"/> false reinstates.</summary>
    public async Task SuspendAsync(int id, bool suspend, string? note, CurrentUser user, CancellationToken ct)
    {
        var p = await db.TradePartners.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Trade partner");
        if (suspend && p.Status != PartnerStatuses.Approved) throw new BusinessRuleException("Only an approved partner can be suspended.");
        if (!suspend && p.Status != PartnerStatuses.Suspended) throw new BusinessRuleException("This partner isn't suspended.");
        if (!suspend && !PriceTiers.IsValid(p.ApprovedTier)) throw new BusinessRuleException("Approve this partner again and choose a tier.");
        p.Status = suspend ? PartnerStatuses.Suspended : PartnerStatuses.Approved;
        if (p.Customer is { } c) c.CustomerType = suspend ? CustomerTypes.Retailer : p.ApprovedTier!;
        p.DecidedAt = clock.UtcNow; p.DecidedBy = user.FullName; p.DecisionNote = Trim(note, 300);
        Audit(user, suspend ? "PARTNER_SUSPENDED" : "PARTNER_REINSTATED", p, note);
        await db.SaveChangesAsync(ct);
        await alerts.TellContactAsync(new ContactAlert(p.ContactName, p.Phone, p.Email, $"{company.LegalName}: your trade account",
            suspend
                ? $"Hello {First(p.ContactName)}, the trade account for {p.BusinessName} is on hold" + (string.IsNullOrWhiteSpace(p.DecisionNote) ? "" : $" ({p.DecisionNote})") + ". Please call us to restore it."
                : $"Hello {First(p.ContactName)}, the trade account for {p.BusinessName} is active again. Your {p.ApprovedTier!.ToLowerInvariant()} prices are back."), ct);
    }

    /// <summary>Removes an application and its documents. A partner with money on record (wallet entries) can only be suspended, never deleted.</summary>
    public async Task DeleteAsync(int id, CurrentUser user, CancellationToken ct)
    {
        var p = await db.TradePartners.Include(x => x.Documents).Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Trade partner");
        if (await db.WalletEntries.AnyAsync(w => w.TradePartnerId == id, ct))
            throw new BusinessRuleException("This partner has wallet records, which must be kept. Suspend them instead.");
        if (p.Status == PartnerStatuses.Approved && p.Customer is { } c) c.CustomerType = CustomerTypes.Retailer;
        db.TradePartners.Remove(p);
        Audit(user, "PARTNER_DELETED", p, p.BusinessName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runs the registry check again (after a provider outage, or once a provider has been set up).</summary>
    public async Task<string> ReverifyAsync(int id, CurrentUser user, CancellationToken ct)
    {
        var p = await db.TradePartners.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Trade partner");
        var check = await VerifySafelyAsync(p.CacNumber, p.BusinessName, ct);
        p.Verification = check.Status; p.VerifiedName = check.RegisteredName; p.VerificationNote = check.Note;
        Audit(user, "PARTNER_REVERIFIED", p, check.Status);
        await db.SaveChangesAsync(ct);
        return check.Status;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<VerificationResult> VerifySafelyAsync(string cac, string name, CancellationToken ct)
    {
        if (!verifier.IsConfigured) return new(PartnerVerifications.NotChecked, null, "No verification provider is set up: check the CAC certificate by hand.");
        try { return await verifier.VerifyAsync(cac, name, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Business verification failed for {Cac}", cac);
            return new(PartnerVerifications.Failed, null, "The registry couldn't be reached. Try Re-check later.");
        }
    }

    private StaffAlert NewApplicationAlert(TradePartner p, bool again)
    {
        var check = p.Verification switch
        {
            PartnerVerifications.Verified => $"CAC verified: {p.VerifiedName}",
            PartnerVerifications.Mismatch => $"CAC belongs to \"{p.VerifiedName}\" (name differs: check closely)",
            PartnerVerifications.NotFound => "CAC number NOT found in the registry",
            PartnerVerifications.Failed => "CAC check failed: registry unreachable",
            _ => "CAC not checked automatically: look at the certificate",
        };
        var text = $"🆕 {(again ? "UPDATED " : "")}TRADE APPLICATION — {company.LegalName}\n{p.BusinessName} wants {p.RequestedTier} prices\n" +
                   $"Contact: {p.ContactName} · {Pretty(p.Phone)} · {p.Email}\nAddress: {p.Address}\nCAC {p.CacNumber}" + (p.TaxId is null ? "" : $" · TIN {p.TaxId}") +
                   $"\n{check}\n\"{p.Description}\"\nApply via {p.Channel}. Approve or reject in Inventory → Trade partners.";
        return new StaffAlert($"{company.LegalName}: trade application from {p.BusinessName} ({p.RequestedTier})", text);
    }

    private static TradePartnerDto Dto(TradePartner p, IReadOnlyList<PartnerDocDto> docs, decimal balance) =>
        new(p.Id, p.BusinessName, p.ContactName, p.Phone, p.Email, p.Address, p.CacNumber, p.TaxId, p.Description, p.RequestedTier, p.ApprovedTier, p.Status, p.Channel,
            p.Verification, p.VerifiedName, p.VerificationNote, p.CustomerId, p.CreatedAt, p.DecidedAt, p.DecidedBy, p.DecisionNote, balance, docs);

    private void Audit(CurrentUser user, string action, TradePartner p, string? detail) =>
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = action, Entity = "TradePartner", EntityId = p.Id == 0 ? null : p.Id.ToString(Inv), At = clock.UtcNow, Detail = Trim(detail, 300) });

    /// <summary>Checks and tidies every field; the messages are written for the applicant.</summary>
    public static PartnerApplication Clean(PartnerApplication a)
    {
        string T(string? s) => (s ?? "").Trim();
        var business = T(a.BusinessName); var contact = T(a.ContactName); var email = T(a.Email).ToLowerInvariant(); var address = T(a.Address);
        var cac = T(a.CacNumber).ToUpperInvariant().Replace(" ", "").Replace("-", ""); var tin = T(a.TaxId); var about = T(a.Description);
        if (business.Length is < 2 or > 150) throw new BusinessRuleException("Please give your business (pet shop) name.");
        if (contact.Length is < 2 or > 100) throw new BusinessRuleException("Please give the name of the person we should speak to.");
        var phone = ChatFlowService.ParsePhone(a.Phone) ?? throw new BusinessRuleException("Please give a phone number we can reach on WhatsApp, e.g. 0803 123 4567.");
        if (email.Length > 150 || !EmailPattern().IsMatch(email)) throw new BusinessRuleException("Please give a valid email address.");
        if (address.Length is < 8 or > 300) throw new BusinessRuleException("Please give your shop's full address.");
        if (!CacPattern().IsMatch(cac)) throw new BusinessRuleException("Please give your CAC number as it appears on your certificate, e.g. RC1234567 or BN1234567.");
        var tinDigits = new string(tin.Where(c => char.IsDigit(c) || c == '-').ToArray());
        if (tin.Length == 0) throw new BusinessRuleException("Please give your Tax Identification Number (TIN).");
        if (!TinPattern().IsMatch(tinDigits)) throw new BusinessRuleException("That TIN doesn't look right. It's usually 8 digits, a dash and 4 digits (12345678-0001), or 10–14 digits.");
        if (about.Length is < 20 or > 1000) throw new BusinessRuleException("Tell us a little about your business (at least 20 characters): what you sell, where, and roughly how much a month.");
        var tier = T(a.RequestedTier) switch
        {
            var s when s.Equals(PriceTiers.Distributor, StringComparison.OrdinalIgnoreCase) => PriceTiers.Distributor,
            var s when s.Equals(PriceTiers.Wholesaler, StringComparison.OrdinalIgnoreCase) || s.Length == 0 => PriceTiers.Wholesaler,
            _ => throw new BusinessRuleException("Choose Wholesaler or Distributor."),
        };
        return new(business, contact, phone, email, address, cac, tinDigits, about, tier);
    }

    private static void CheckUpload(PartnerUpload u)
    {
        if (u.Kind is not ("cac" or "id" or "other")) throw new BusinessRuleException("Unknown document type.");
        if (u.Data.Length == 0) throw new BusinessRuleException("The file you attached is empty.");
        if (u.Data.Length > MaxDocumentBytes) throw new BusinessRuleException("Please attach a file under 3 MB (a photo or a PDF scan is fine).");
        if (!AllowedTypes.Contains(u.ContentType) || !MagicMatches(u.Data, u.ContentType))
            throw new BusinessRuleException("Attach a PDF, JPG, PNG or WebP file.");
    }

    /// <summary>The file's first bytes must agree with the type it claims, so a renamed executable or script is refused.</summary>
    public static bool MagicMatches(byte[] d, string type) => type switch
    {
        "application/pdf" => d.Length > 4 && d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44 && d[3] == 0x46,
        "image/jpeg" => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF,
        "image/png" => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47,
        "image/webp" => d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50,
        _ => false,
    };

    private static string SafeName(string name)
    {
        var n = new string(Path.GetFileName(name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').ToArray()).Trim();
        return n.Length == 0 ? "document" : n.Length > 150 ? n[^150..] : n;
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
    private static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
    /// <summary>2348031234567 → 0803 123 4567.</summary>
    public static string Pretty(string phone) => phone.StartsWith("234") && phone.Length == 13 ? $"0{phone[3..6]} {phone[6..9]} {phone[9..]}" : "+" + phone;
}
