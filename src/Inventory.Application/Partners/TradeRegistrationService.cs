using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inventory.Application.Abstractions;
using Inventory.Application.AppsEngine;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Partners;

public sealed record RegFile(int Id, string Kind, string FileName, int Size);
/// <summary>Where an application stands: the furthest step reached, the answers so far and the files uploaded. The email is shown masked.</summary>
public sealed record RegState(int Step, string Email, bool EmailVerified, IReadOnlyDictionary<string, string> Data, IReadOnlyList<RegFile> Files, IReadOnlyList<RegStore> Stores);
public sealed record RegStore(string Key, string Name);
/// <param name="Next">"code" (a code was emailed), "resume" (this device carries on), "login" (an existing partner: a sign-in code was emailed).</param>
public sealed record RegStart(string Next, string Email, string? DeviceToken, RegState? State, string Message);
/// <param name="Partner">True when the code signed an existing partner in (their dashboard opens), false when it confirmed an application's email.</param>
public sealed record RegVerified(bool Partner, string Email, string? DeviceToken, RegState? State);
public sealed record RegSubmitted(string Message, IReadOnlyList<string> Stores);

/// <summary>
/// The website's step-by-step trade registration. Steps: 1 email (confirmed with a code), 2 business, 3 contact, 4 CAC and TIN, 5 documents, 6 review and send.
/// Every step is saved, so the applicant carries on where they stopped: on the same device by its token, on another device after confirming their email.
/// An email that already belongs to a partner signs them in instead. Untouched drafts are followed up at 3, 7 and 21 days and removed 24 hours after the
/// final (warning) email, which frees the address. Drafts live in the first trade store's database; the finished application goes to every store chosen.
/// </summary>
public sealed partial class TradeRegistrationService(IBusinessDbContext db, IClock clock, IEmailSender email, ICompanyScopes stores, ILogger<TradeRegistrationService> log)
{
    public const int LastStep = 6;
    private static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(15);
    private static readonly string[] Step2 = ["businessName", "tier", "stores", "description"], Step3 = ["contactName", "phone", "address"], Step4 = ["cacNumber", "taxId"];
    public static readonly (int Days, int Count)[] FollowUps = [(3, 1), (7, 2), (21, 3)];
    public static readonly TimeSpan WarningGrace = TimeSpan.FromHours(24);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")] private static partial Regex EmailPattern();

    // ------------------------------------------------------------------ step 1: email

    public async Task<RegStart> StartAsync(string? rawEmail, string? deviceToken, CancellationToken ct)
    {
        var mail = CleanEmail(rawEmail);
        // an existing partner (approved, waiting, on hold) signs in to see their account
        if (await PartnerStoresAsync(mail, ct) is { Count: > 0 })
        {
            await SendCodeAsync(mail, EmailCodePurposes.Login, ct);
            return new RegStart("login", Mask(mail), null, null, $"Welcome back. We've emailed a 6-digit code to {Mask(mail)} to sign you in.");
        }
        var draft = await db.TradeApplicationDrafts.Include(d => d.Files).FirstOrDefaultAsync(d => d.Email == mail, ct);
        if (draft is not null && draft.EmailVerifiedAt is not null && deviceToken is { Length: > 0 } && Hash(deviceToken) == draft.DeviceTokenHash)
        {
            Touch(draft); await db.SaveChangesAsync(ct);
            return new RegStart("resume", Mask(mail), null, State(draft), $"Welcome back. You stopped at step {draft.Step} of {LastStep}.");
        }
        string? token = null;
        if (draft is null)
        {
            token = NewToken();
            draft = new TradeApplicationDraft { Email = mail, DeviceTokenHash = Hash(token), CreatedAt = clock.UtcNow, LastActivityAt = clock.UtcNow };
            db.TradeApplicationDrafts.Add(draft);
            await db.SaveChangesAsync(ct);
        }
        await SendCodeAsync(mail, EmailCodePurposes.Verify, ct);
        return new RegStart("code", Mask(mail), token, null, draft.EmailVerifiedAt is null
            ? $"We've emailed a 6-digit code to {Mask(mail)}. Enter it to confirm your email."
            : $"You've started before. We've emailed a code to {Mask(mail)} so you can carry on where you stopped.");
    }

    /// <summary>Checks a code. For an application it confirms the email and binds this device; for a partner it is a sign-in.</summary>
    public async Task<RegVerified> VerifyAsync(string? rawEmail, string? code, string? deviceToken, CancellationToken ct)
    {
        var mail = CleanEmail(rawEmail);
        var c = await db.EmailCodes.Where(x => x.Email == mail && x.UsedAt == null && x.ExpiresAt > clock.UtcNow).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("That code has expired. Ask for a new one.");
        if (c.Attempts >= 5) throw new BusinessRuleException("Too many wrong codes. Ask for a new one.");
        c.Attempts++;
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(digits)), Encoding.ASCII.GetBytes(c.CodeHash)))
        {
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException(c.Attempts >= 5 ? "Too many wrong codes. Ask for a new one." : "That code isn't right. Check the latest email from us.");
        }
        c.UsedAt = clock.UtcNow;
        if (c.Purpose == EmailCodePurposes.Login) { await db.SaveChangesAsync(ct); return new RegVerified(true, mail, null, null); }

        var draft = await db.TradeApplicationDrafts.Include(d => d.Files).FirstOrDefaultAsync(d => d.Email == mail, ct) ?? throw new BusinessRuleException("We couldn't find your application. Please start again.");
        draft.EmailVerifiedAt ??= clock.UtcNow;
        if (draft.Step < 2) draft.Step = 2;
        // this device carries on from now (a new token if it isn't the one that started)
        string? token = null;
        if (deviceToken is not { Length: > 0 } || Hash(deviceToken) != draft.DeviceTokenHash) { token = NewToken(); draft.DeviceTokenHash = Hash(token); }
        Touch(draft);
        await db.SaveChangesAsync(ct);
        return new RegVerified(false, mail, token, State(draft));
    }

    public async Task ResendAsync(string? rawEmail, CancellationToken ct)
    {
        var mail = CleanEmail(rawEmail);
        var purpose = await PartnerStoresAsync(mail, ct) is { Count: > 0 } ? EmailCodePurposes.Login : EmailCodePurposes.Verify;
        if (purpose == EmailCodePurposes.Verify && !await db.TradeApplicationDrafts.AnyAsync(d => d.Email == mail, ct)) throw new BusinessRuleException("Start with your email first.");
        await SendCodeAsync(mail, purpose, ct);
    }

    // ------------------------------------------------------------------ steps 2–6

    /// <summary>The application this device is filling in, or null.</summary>
    public async Task<RegState?> ResumeAsync(string? deviceToken, CancellationToken ct) =>
        await DraftAsync(deviceToken, ct) is { } d ? State(d) : null;

    public async Task<RegState> SaveStepAsync(string? deviceToken, int step, JsonObject fields, CancellationToken ct)
    {
        var d = await RequireAsync(deviceToken, ct);
        if (step is < 2 or > 4) throw new BusinessRuleException("Unknown step.");
        if (step > d.Step) throw new BusinessRuleException("Please finish the earlier steps first.");
        var keys = step switch { 2 => Step2, 3 => Step3, _ => Step4 };
        var data = Parse(d.DataJson);
        foreach (var k in keys) data[k] = (fields[k]?.ToString() ?? "").Trim();
        Check(step, data, d.Email);
        d.DataJson = data.ToJsonString();
        d.Step = Math.Max(d.Step, step + 1);
        Touch(d);
        await db.SaveChangesAsync(ct);
        return State(d);
    }

    public async Task<RegState> UploadAsync(string? deviceToken, string kind, string fileName, string contentType, byte[] bytes, CancellationToken ct)
    {
        var d = await RequireAsync(deviceToken, ct);
        if (d.Step < 5) throw new BusinessRuleException("Please finish the earlier steps first.");
        if (kind is not ("cac" or "id" or "other")) throw new BusinessRuleException("Unknown document type.");
        if (bytes.Length == 0) throw new BusinessRuleException("The file you attached is empty.");
        if (bytes.Length > TradePartnerService.MaxDocumentBytes) throw new BusinessRuleException("Please attach a file under 3 MB (a photo or a PDF scan is fine).");
        if (!TradePartnerService.MagicMatches(bytes, contentType)) throw new BusinessRuleException("Attach a PDF, JPG, PNG or WebP file.");
        d.Files.RemoveAll(f => f.Kind == kind);
        d.Files.Add(new TradeApplicationFile { Kind = kind, FileName = SafeName(fileName), ContentType = contentType, Data = bytes, UploadedAt = clock.UtcNow });
        Touch(d);
        await db.SaveChangesAsync(ct);
        return State(d);
    }

    public async Task<RegState> RemoveFileAsync(string? deviceToken, int fileId, CancellationToken ct)
    {
        var d = await RequireAsync(deviceToken, ct);
        d.Files.RemoveAll(f => f.Id == fileId);
        Touch(d);
        await db.SaveChangesAsync(ct);
        return State(d);
    }

    /// <summary>Step 5 done: the CAC certificate is required before the review.</summary>
    public async Task<RegState> DocumentsDoneAsync(string? deviceToken, CancellationToken ct)
    {
        var d = await RequireAsync(deviceToken, ct);
        if (d.Step < 5) throw new BusinessRuleException("Please finish the earlier steps first.");
        if (!d.Files.Any(f => f.Kind == "cac")) throw new BusinessRuleException("Please attach your CAC certificate (PDF or photo).");
        d.Step = LastStep; Touch(d);
        await db.SaveChangesAsync(ct);
        return State(d);
    }

    /// <summary>Sends the finished application to every chosen store (each runs its own checks and tells its admin), then removes the draft.</summary>
    public async Task<RegSubmitted> SubmitAsync(string? deviceToken, CancellationToken ct)
    {
        var d = await RequireAsync(deviceToken, ct);
        if (d.Step < LastStep) throw new BusinessRuleException("Please finish every step first.");
        var data = Parse(d.DataJson);
        var app = TradePartnerService.Clean(Application(data, d.Email));
        var uploads = d.Files.Select(f => new PartnerUpload(f.Kind, f.FileName, f.ContentType, f.Data)).ToList();
        var chosen = StoresOf(data);
        var message = "";
        foreach (var key in chosen)
            message = (await stores.InAsync(key, sp => sp.GetRequiredService<TradePartnerService>().ApplyAsync(app, uploads, "website", ct))).Message;
        db.TradeApplicationDrafts.Remove(d);
        await db.SaveChangesAsync(ct);
        return new RegSubmitted(message, chosen.Select(stores.DisplayName).ToList());
    }

    // ------------------------------------------------------------------ partners (sign-in)

    /// <summary>The stores where this email belongs to a trade partner (any status).</summary>
    public async Task<List<string>> PartnerStoresAsync(string mail, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var key in stores.TradeStores)
            if (await stores.InAsync(key, sp => sp.GetRequiredService<IBusinessDbContext>().TradePartners.AnyAsync(p => p.Email == mail, ct))) found.Add(key);
        return found;
    }

    // ------------------------------------------------------------------ follow-ups

    /// <summary>Emails drafts that have gone quiet (3, 7 and 21 days; the last is a warning) and deletes those still untouched 24 hours after the warning.</summary>
    public async Task<(int Emailed, int Deleted)> FollowUpAsync(string siteUrl, CancellationToken ct)
    {
        var now = clock.UtcNow; int emailed = 0, deleted = 0;
        // never confirmed: nobody to write to, quietly gone after a week
        deleted += await db.TradeApplicationDrafts.Where(d => d.EmailVerifiedAt == null && d.LastActivityAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);

        var expired = await db.TradeApplicationDrafts.Where(d => d.WarnedAt != null && d.WarnedAt < now - WarningGrace && d.LastActivityAt <= d.WarnedAt).ToListAsync(ct);
        foreach (var d in expired) { db.TradeApplicationDrafts.Remove(d); deleted++; log.LogInformation("Trade draft {Id} removed after the final warning", d.Id); }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);

        if (!email.IsConfigured) return (emailed, deleted);
        var quiet = await db.TradeApplicationDrafts.Include(d => d.Files).Where(d => d.EmailVerifiedAt != null && d.RemindersSent < 3 && d.LastActivityAt < now.AddDays(-3)).ToListAsync(ct);
        foreach (var d in quiet)
        {
            // in order: the 3-day note first, then 7 days, then the 21-day warning (all counted from the last activity)
            var due = FollowUps[d.RemindersSent];
            if (d.LastActivityAt > now.AddDays(-due.Days)) continue;
            var (subject, text) = FollowUpText(due.Count, d, siteUrl);
            try { await email.SendAsync(new EmailMessage(d.Email, Parse(d.DataJson).Str("contactName"), subject, Html(text), text, [], "Chewy Pet Trade", null), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Trade follow-up to draft {Id} failed", d.Id); continue; }
            d.RemindersSent = due.Count; d.LastReminderAt = now;
            if (due.Count == 3) d.WarnedAt = now;
            emailed++;
        }
        await db.SaveChangesAsync(ct);
        return (emailed, deleted);
    }

    public static (string Subject, string Text) FollowUpText(int n, TradeApplicationDraft d, string siteUrl)
    {
        var data = Parse(d.DataJson);
        var name = (data.Str("contactName") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";
        var shop = data.Str("businessName") is { Length: > 0 } b ? b : "your shop";
        var link = siteUrl.TrimEnd('/') + "/#trade";
        var where = d.Files.Any(f => f.Kind == "cac") ? $"You're on step {d.Step} of {LastStep}." : $"You're on step {d.Step} of {LastStep}; the main thing left is a photo or PDF of your CAC certificate.";
        return n switch
        {
            1 => ($"{shop}: your trade prices are a few steps away",
                $"Hello {name},\n\nThanks for starting your trade application with Chewy Pet. {where}\n\nIt only takes a couple of minutes to finish, and once a manager approves {shop} you'll see wholesale or distributor prices every time you order.\n\nCarry on where you stopped: {link} (enter this email and we'll take you straight back).\n\nAny questions? Just reply to this email.\n\nChewy Pet Trade"),
            2 => ($"Still want trade prices for {shop}?",
                $"Hello {name},\n\nA quick nudge: your trade application is saved and waiting. {where}\n\nPartners order straight from the source at trade prices, with orders handled on WhatsApp or online. Finish here: {link}\n\nIf something is holding you up (finding the CAC certificate, a question about the tiers), reply and we'll help.\n\nChewy Pet Trade"),
            _ => ($"Last reminder: your {shop} application closes in 24 hours",
                $"Hello {name},\n\nYour trade application hasn't moved in three weeks, so we'll close it in 24 hours and delete what you've entered. {where}\n\nTo keep it, just continue here before then: {link}\n\nIf it closes, you can always start again with the same email.\n\nChewy Pet Trade"),
        };
    }

    // ------------------------------------------------------------------ helpers

    private async Task SendCodeAsync(string mail, string purpose, CancellationToken ct)
    {
        if (!email.IsConfigured) throw new BusinessRuleException("We can't send email right now. Please apply on WhatsApp instead.");
        var now = clock.UtcNow;
        var recent = await db.EmailCodes.Where(x => x.Email == mail && x.CreatedAt > now.AddHours(-1)).Select(x => x.CreatedAt).ToListAsync(ct);
        if (recent.Any(t => t > now.AddSeconds(-60))) throw new BusinessRuleException("We've just sent you a code. Please wait a minute before asking again.");
        if (recent.Count >= 6) throw new BusinessRuleException("Too many codes asked for. Please try again in an hour.");
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.EmailCodes.Add(new EmailCode { Email = mail, Purpose = purpose, CodeHash = Hash(code), CreatedAt = now, ExpiresAt = now + CodeLife });
        await db.SaveChangesAsync(ct);
        var what = purpose == EmailCodePurposes.Login ? "sign in to your partner account" : "confirm your email for your trade application";
        var text = $"Your code is {code}\n\nEnter it on the website to {what}. It works for 15 minutes.\n\nIf you didn't ask for this, you can ignore this email.\n\nChewy Pet Trade";
        var html = $"<div style=\"font:15px Segoe UI,Arial,sans-serif;color:#1c2130;line-height:1.5\"><p>Your code is</p><p style=\"font:700 32px/1 monospace;letter-spacing:8px;margin:8px 0 16px\">{code}</p>"
                 + $"<p>Enter it on the website to {WebUtility.HtmlEncode(what)}. It works for 15 minutes.</p><p style=\"color:#777;font-size:13px\">If you didn't ask for this, you can ignore this email.</p></div>";
        try { await email.SendAsync(new EmailMessage(mail, null, $"{code} is your Chewy Pet code", html, text, [], "Chewy Pet Trade", null), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Code email failed");
            throw new BusinessRuleException("We couldn't send the email. Check the address and try again.");
        }
    }

    private static void Check(int step, JsonObject data, string mail)
    {
        // the same rules as the final submission, applied to this step's fields only (the other fields get known-good stand-ins)
        string V(string k, string fallback) => data.Str(k) is { Length: > 0 } v ? v : fallback;
        var app = step switch
        {
            2 => new PartnerApplication(V("businessName", ""), "Stand In", "08030000000", mail, "1 Stand-in Street", "RC123456", "12345678-0001", V("description", ""), V("tier", "")),
            3 => new PartnerApplication("Stand In", V("contactName", ""), V("phone", ""), mail, V("address", ""), "RC123456", "12345678-0001", new string('x', 20), "Wholesaler"),
            _ => new PartnerApplication("Stand In", "Stand In", "08030000000", mail, "1 Stand-in Street", V("cacNumber", ""), data.Str("taxId"), new string('x', 20), "Wholesaler"),
        };
        TradePartnerService.Clean(app);
        if (step == 2 && data.Str("stores") is not ("both" or "chewypets" or "candid")) throw new BusinessRuleException("Choose which store you want to buy from.");
    }

    private List<string> StoresOf(JsonObject data)
    {
        var s = data.Str("stores") ?? "both";
        var all = stores.TradeStores;
        return s == "both" ? all.ToList() : all.Where(k => k == s).DefaultIfEmpty(all[0]).ToList();
    }

    private static PartnerApplication Application(JsonObject d, string mail) =>
        new(d.Str("businessName") ?? "", d.Str("contactName") ?? "", d.Str("phone") ?? "", mail, d.Str("address") ?? "", d.Str("cacNumber") ?? "", d.Str("taxId"), d.Str("description") ?? "", d.Str("tier") ?? "");

    private RegState State(TradeApplicationDraft d)
    {
        var data = Parse(d.DataJson);
        var shown = data.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString());
        return new RegState(d.Step, Mask(d.Email), d.EmailVerifiedAt is not null, shown, d.Files.Select(f => new RegFile(f.Id, f.Kind, f.FileName, f.Data.Length)).ToList(),
            stores.TradeStores.Select(k => new RegStore(k, stores.DisplayName(k))).ToList());
    }

    private async Task<TradeApplicationDraft?> DraftAsync(string? token, CancellationToken ct)
    {
        if (token is not { Length: > 20 }) return null;
        var h = Hash(token);
        return await db.TradeApplicationDrafts.Include(d => d.Files).FirstOrDefaultAsync(d => d.DeviceTokenHash == h, ct);
    }

    private async Task<TradeApplicationDraft> RequireAsync(string? token, CancellationToken ct)
    {
        var d = await DraftAsync(token, ct) ?? throw new BusinessRuleException("Your application wasn't found on this device. Enter your email to carry on.");
        if (d.EmailVerifiedAt is null) throw new BusinessRuleException("Please confirm your email first.");
        return d;
    }

    /// <summary>Any activity puts the follow-up clock back to zero.</summary>
    private void Touch(TradeApplicationDraft d) { d.LastActivityAt = clock.UtcNow; d.RemindersSent = 0; d.WarnedAt = null; }

    public static string CleanEmail(string? raw)
    {
        var m = (raw ?? "").Trim().ToLowerInvariant();
        if (m.Length > 150 || !EmailPattern().IsMatch(m)) throw new BusinessRuleException("Please enter a valid email address.");
        return m;
    }

    /// <summary>bola@example.com → b***a@example.com</summary>
    public static string Mask(string mail)
    {
        var at = mail.IndexOf('@');
        if (at <= 1) return mail;
        var user = mail[..at];
        return (user.Length <= 2 ? user[0] + "*" : user[0] + new string('*', Math.Min(5, user.Length - 2)) + user[^1]) + mail[at..];
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static JsonObject Parse(string json) { try { return JsonNode.Parse(json) as JsonObject ?? []; } catch { return []; } }
    private static string SafeName(string name)
    {
        var n = new string(Path.GetFileName(name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').ToArray()).Trim();
        return n.Length == 0 ? "document" : n.Length > 150 ? n[^150..] : n;
    }
    private static string Html(string text) =>
        $"<div style=\"font:15px Segoe UI,Arial,sans-serif;color:#1c2130;line-height:1.55;white-space:pre-line\">{WebUtility.HtmlEncode(text)}</div>";
}
