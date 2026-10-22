using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Inventory.Api.Infrastructure;
using Inventory.Application.Common;
using Inventory.Application.Payments;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Controllers;

public sealed class SiteOptions
{
    public const string Section = "Site";
    /// <summary>The public address customers use, e.g. https://chewypetsfeeds.com. Empty = documents link straight to a Paystack checkout.</summary>
    public string PublicUrl { get; set; } = "";
}

/// <summary>
/// Builds and reads the /pay/… links on receipts and quotations. The token is the server's own authenticated encryption of
/// "company|docType|docId" (ASP.NET Data Protection), so it can't be forged or edited to point at another document, and it names the
/// business so the page opens the right company's records.
/// </summary>
public sealed class PayPageLinks(IDataProtectionProvider dp, IOptions<SiteOptions> site, Inventory.Application.Abstractions.ICompanyContext company) : IPayPageLinks
{
    internal const string Purpose = "pay-page.v1";

    public string? UrlFor(string docType, int docId)
    {
        var baseUrl = site.Value.PublicUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return $"{baseUrl}/pay/{dp.CreateProtector(Purpose).Protect($"{company.Key}|{docType}|{docId.ToString(CultureInfo.InvariantCulture)}")}";
    }

    public static bool TryRead(IDataProtectionProvider dp, string token, out string companyKey, out string docType, out int docId)
    {
        companyKey = docType = ""; docId = 0;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 600) return false;
        try
        {
            var parts = dp.CreateProtector(Purpose).Unprotect(token).Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out docId)) return false;
            if (parts[1] != PaymentDocTypes.Invoice && parts[1] != PaymentDocTypes.Quotation) return false;
            (companyKey, docType) = (parts[0], parts[1]);
            return true;
        }
        catch (CryptographicException) { return false; }
    }
}

/// <summary>
/// The customer-facing "choose how to pay" page behind the PAY ONLINE button on receipts and quotations. Plain server-rendered HTML with inline
/// styles and no script (works on any phone, and under the site's strict content-security policy). Choosing a processor opens a fresh
/// checkout for what is owed at that moment; the processor sends the customer back here, where the payment is confirmed with the processor.
/// </summary>
[ApiController, Route("pay"), AllowAnonymous]
[EnableRateLimiting(Policies.WebChatRateLimit)]
public class PayPageController(IDataProtectionProvider dp, CompanyRegistry registry, ILogger<PayPageController> log) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Page(string token, CancellationToken ct)
    {
        if (!Open(token, out var docType, out var docId, out var companyKey)) return Html(NotFoundPage(), 404);
        var pages = HttpContext.RequestServices.GetRequiredService<PayPageService>();
        var info = await pages.GetAsync(docType, docId, ct);
        if (info.Awaiting)
        {
            // Back from the processor (or checking again): ask it directly, so "paid" shows now rather than when the webhook lands.
            try
            {
                var settled = await pages.ConfirmAsync(docType, docId, ct);
                if (settled is { Applied: true })
                {
                    await HttpContext.RequestServices.GetRequiredService<OrderDispatchService>().CompleteAsync(settled, ct);
                    info = await pages.GetAsync(docType, docId, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Pay page: confirming a pending payment failed"); }
        }
        return Html(Render(info, token, companyKey, error: null));
    }

    [HttpGet("{token}/{provider}")]
    public async Task<IActionResult> Go(string token, string provider, CancellationToken ct)
    {
        if (!Open(token, out var docType, out var docId, out var companyKey)) return Html(NotFoundPage(), 404);
        if (!PaymentProviders.IsValid(provider)) return Redirect($"/pay/{token}");
        var pages = HttpContext.RequestServices.GetRequiredService<PayPageService>();
        var back = $"{Request.Scheme}://{Request.Host}/pay/{token}";
        try { return Redirect(await pages.StartAsync(docType, docId, provider, back, ct)); }
        catch (BusinessRuleException ex)
        {
            return Html(Render(await pages.GetAsync(docType, docId, ct), token, companyKey, ex.Message));
        }
    }

    private bool Open(string token, out string docType, out int docId, out string companyKey)
    {
        // Every answer here (valid link or not) is private to one customer: never cached, never indexed.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (!PayPageLinks.TryRead(dp, token, out companyKey, out docType, out docId) || registry.Find(companyKey) is null) return false;
        HttpContext.Items[RequestCompany.OverrideItem] = companyKey;   // same company routing as the payment webhooks
        return true;
    }

    private ContentResult Html(string body, int status = 200) => new() { Content = body, ContentType = "text/html; charset=utf-8", StatusCode = status };

    // ------------------------------------------------------------------------------------------------ markup
    // The page continues the receipt the customer is holding: a white slip that feeds up as if printed, the amount in large quiet figures,
    // a tear-line, then the ways to pay. One accent (the business's colour), system fonts, no script — motion is CSS and switches off when the
    // phone asks for reduced motion.
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static (string Accent, string Tint) Colours(string companyKey) => companyKey == "candid" ? ("#6A2C5B", "#F4EEF2") : ("#1F6B4F", "#EDF4F0");

    /// <summary>"₦11,500.00" as naira sign, whole naira and kobo, so the whole figure can be large and the rest quiet.</summary>
    private static string Figure(decimal v)
    {
        var s = v.ToString("N2", CultureInfo.InvariantCulture);
        var dot = s.LastIndexOf('.');
        return $"<span class=\"cur\">₦</span><span class=\"whole\">{s[..dot]}</span><span class=\"kobo\">{s[dot..]}</span>";
    }

    private static (string Name, string Blurb, string Mark, string Colour) Provider(string p) => p switch
    {
        PaymentProviders.AlatPay => ("AlatPay", "Card or bank transfer · Wema Bank", "A", "#7A2B86"),
        PaymentProviders.Paystack => ("Paystack", "Card, bank transfer or USSD", "P", "#0B9BD1"),
        _ => (p, "Pay online", p.Length > 0 ? p[..1].ToUpperInvariant() : "•", "#16202A"),
    };

    private const string Lock = "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><rect x=\"3\" y=\"7\" width=\"10\" height=\"7\" rx=\"1.6\"/><path d=\"M5.5 7V5a2.5 2.5 0 0 1 5 0v2\"/></svg>";
    private const string Arrow = "<svg class=\"arr\" viewBox=\"0 0 20 20\" aria-hidden=\"true\"><path d=\"M7 4.5 12.5 10 7 15.5\"/></svg>";

    private static string Render(PayPageInfo i, string token, string companyKey, string? error)
    {
        var (accent, tint) = Colours(companyKey);
        var kind = i.DocKind.ToLowerInvariant();
        var b = new System.Text.StringBuilder();
        b.Append($"<header class=\"top r1\"><span class=\"brand\">{E(i.Business)}</span><span class=\"secure\">{Lock}Secure payment</span></header>");

        if (i.Closed is not null)
        {
            b.Append("<section class=\"done r2\"><svg class=\"check\" viewBox=\"0 0 52 52\" aria-hidden=\"true\"><circle cx=\"26\" cy=\"26\" r=\"23\"/><path d=\"M15 27.5l7.2 7L37 19.5\"/></svg>" +
                     $"<p class=\"done-t\">{E(i.Closed)}</p></section>");
        }
        else
        {
            b.Append($"<section class=\"amount r2\"><p class=\"k\">Amount due</p><p class=\"figure\">{Figure(i.Amount)}</p></section>");
        }
        b.Append($"<dl class=\"meta r3\"><div><dt>{E(i.DocKind)}</dt><dd class=\"mono\">{E(i.DocNumber)}</dd></div><div><dt>Billed to</dt><dd>{E(i.Customer)}</dd></div></dl>");

        if (i.Closed is null)
        {
            b.Append("<div class=\"tear\" aria-hidden=\"true\"></div><section class=\"pay r4\">");
            if (error is not null) b.Append($"<p class=\"alert\" role=\"alert\">{E(error)}</p>");
            if (i.Providers.Count == 0)
                b.Append($"<p class=\"alert\">Online payment isn't available right now. Please pay by bank transfer using the details on your {E(kind)}.</p>");
            else
            {
                b.Append("<p class=\"label\">Pay with</p><ul class=\"opts\">");
                // AlatPay first (and so the filled button), then the rest in their usual order.
                var first = true;
                foreach (var p in i.Providers.OrderBy(x => x == PaymentProviders.AlatPay ? 0 : 1))
                {
                    var (name, blurb, mark, colour) = Provider(p);
                    b.Append($"<li><a class=\"opt{(first ? " primary" : "")}\" href=\"/pay/{E(token)}/{E(p)}\">" +
                             $"<span class=\"mark\" style=\"--m:{colour}\">{mark}</span><span class=\"txt\"><b>{E(name)}</b><small>{E(blurb)}</small></span>{Arrow}</a></li>");
                    first = false;
                }
                b.Append("</ul>");
                if (i.Awaiting) b.Append("<p class=\"hint\">Already paid? It can take a minute to show here — refresh before paying again.</p>");
                b.Append($"<p class=\"fine\">{Lock}You'll finish on the provider's secure page, then return here. We never see your card details.</p>");
            }
            b.Append("</section>");
        }
        if (i.Phone is { } phone)
            b.Append($"<footer class=\"help r5\">Questions about this {E(kind)}? <a href=\"tel:{E(new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray()))}\">{E(phone)}</a></footer>");

        return Shell($"Pay {E(kind)} {E(i.DocNumber)} · {E(i.Business)}", accent, tint, b.ToString());
    }

    private static string NotFoundPage() => Shell("Payment link not recognised", "#1F6B4F", "#EDF4F0",
        "<header class=\"top r1\"><span class=\"brand\">Payment</span><span class=\"secure\">" + Lock + "Secure payment</span></header>" +
        "<section class=\"amount r2\"><p class=\"k\">Link not recognised</p><p class=\"lead\">This payment link isn't valid. Please use the PAY ONLINE button or the " +
        "“Scan to pay” code on your latest receipt or quotation, or contact us.</p></section>");

    private static string Shell(string title, string accent, string tint, string body) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">" +
        $"<meta name=\"robots\" content=\"noindex,nofollow\"><meta name=\"theme-color\" content=\"{accent}\"><title>{title}</title>" +
        $"<style>:root{{--accent:{accent};--tint:{tint}}}</style><style>{Css}</style></head><body><main class=\"slip\">{body}</main></body></html>";

    private const string Css = """
        :root{--ink:#17212B;--ink2:#3A4650;--muted:#6B7780;--line:#E1E5E2;--page:#EEF0EE;--bad:#B3261E}
        *{box-sizing:border-box}html{-webkit-text-size-adjust:100%}body{margin:0}
        body{min-height:100vh;min-height:100dvh;display:grid;grid-template-columns:minmax(0,420px);justify-content:center;align-content:center;
             padding:28px 16px calc(28px + env(safe-area-inset-bottom));
             background:var(--page);color:var(--ink);font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",sans-serif;
             -webkit-font-smoothing:antialiased;font-feature-settings:"tnum" 1}
        .slip{position:relative;width:100%;max-width:420px;background:#fff;border-radius:14px;
              box-shadow:0 1px 0 rgba(23,33,43,.04),0 18px 40px -24px rgba(23,33,43,.35);
              animation:feed .75s cubic-bezier(.22,.8,.24,1) both}
        .slip::before{content:"";position:absolute;inset:0 0 auto;height:4px;border-radius:14px 14px 0 0;background:var(--accent)}

        .top{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:20px 22px 0}
        .brand{min-width:0;font-size:12px;line-height:1.35;font-weight:650;letter-spacing:.06em;text-transform:uppercase;color:var(--ink2)}
        .top{align-items:flex-start}
        .secure{flex:none;display:inline-flex;align-items:center;gap:5px;font-size:12px;color:var(--muted)}
        svg{width:14px;height:14px;fill:none;stroke:currentColor;stroke-width:1.6;stroke-linecap:round;stroke-linejoin:round}

        .amount{padding:22px 22px 4px}
        .k{margin:0;font-size:13px;color:var(--muted)}
        .figure{margin:2px 0 0;display:flex;align-items:baseline;gap:2px;letter-spacing:-.02em;line-height:1.05}
        .cur{font-size:24px;font-weight:600;color:var(--ink2);margin-right:2px}
        .whole{font-size:44px;font-weight:700}
        .kobo{font-size:22px;font-weight:600;color:var(--muted)}
        .lead{margin:6px 0 4px;font-size:15px;color:var(--ink2)}

        .meta{margin:14px 22px 0;padding:0;display:grid;gap:8px}
        .meta div{display:flex;justify-content:space-between;gap:16px;align-items:baseline}
        .meta dt{flex:none;font-size:13px;color:var(--muted)}
        .meta dd{margin:0;min-width:0;font-weight:600;text-align:right;overflow-wrap:anywhere;word-break:break-word}
        .mono{font-family:ui-monospace,"SF Mono",Consolas,monospace;font-size:13px;font-weight:500;letter-spacing:0;word-break:break-all}
        .slip,.top,.opt,.txt{min-width:0}.txt small{overflow-wrap:anywhere}

        /* the tear-line: a dashed rule with two half-circle bites out of the slip's edges */
        .tear{position:relative;height:1px;margin:22px 0 0;background-image:linear-gradient(90deg,var(--line) 55%,transparent 0);background-size:9px 1px}
        .tear::before,.tear::after{content:"";position:absolute;top:-9px;width:18px;height:18px;border-radius:50%;background:var(--page)}
        .tear::before{left:-9px}.tear::after{right:-9px}

        .pay{padding:18px 22px 22px}
        .label{margin:0 0 10px;font-size:13px;font-weight:600;color:var(--ink2)}
        .opts{list-style:none;margin:0;padding:0;display:grid;gap:10px}
        .opt{display:flex;align-items:center;gap:12px;padding:13px 14px;border:1px solid var(--line);border-radius:12px;background:#fff;color:var(--ink);text-decoration:none;
             transition:border-color .18s,background-color .18s,box-shadow .18s,transform .12s}
        .opt:hover{border-color:#C9D0CB;box-shadow:0 6px 18px -12px rgba(23,33,43,.35)}
        .opt:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
        .opt:active{transform:scale(.985)}
        .opt.primary{background:var(--accent);border-color:var(--accent);color:#fff}
        .opt.primary small{color:rgba(255,255,255,.78)}
        .opt.primary .mark{background:#fff;color:var(--m)}
        .opt.primary:hover{box-shadow:0 10px 24px -14px var(--accent)}
        .mark{flex:none;display:grid;place-items:center;width:36px;height:36px;border-radius:9px;background:var(--m);color:#fff;font-weight:750;font-size:16px}
        .txt{flex:1;display:grid;min-width:0}
        .txt b{font-size:16px;font-weight:650}
        .txt small{font-size:12.5px;color:var(--muted)}
        .arr{flex:none;width:18px;height:18px;stroke-width:2;opacity:.75;transition:transform .18s}
        .opt:hover .arr{transform:translateX(3px);opacity:1}

        .hint{margin:12px 2px 0;font-size:13px;color:var(--muted)}
        .fine{margin:14px 2px 0;display:flex;gap:6px;align-items:flex-start;font-size:12px;color:var(--muted)}
        .fine svg{flex:none;margin-top:2px;width:12px;height:12px}
        .alert{margin:0 0 12px;padding:10px 12px;border-radius:10px;background:#FCEEEC;color:var(--bad);font-size:14px;font-weight:550}

        .done{display:flex;align-items:center;gap:14px;padding:22px 22px 4px}
        .check{flex:none;width:52px;height:52px;stroke:var(--accent);stroke-width:2.6}
        .check circle{stroke-dasharray:145;stroke-dashoffset:145;animation:draw .7s .45s ease-out forwards}
        .check path{stroke-dasharray:32;stroke-dashoffset:32;animation:draw .35s 1s ease-out forwards}
        .done-t{margin:0;font-size:16px;font-weight:600}

        .help{margin:0;padding:14px 22px 18px;border-top:1px solid var(--line);font-size:13px;color:var(--muted)}
        .help a{color:var(--accent);font-weight:600;text-decoration:none}
        .help a:hover{text-decoration:underline}

        /* motion: the slip feeds up as if printed, then its lines settle in */
        .r1,.r2,.r3,.r4,.r5{animation:rise .5s cubic-bezier(.22,.8,.24,1) both}
        .r1{animation-delay:.28s}.r2{animation-delay:.36s}.r3{animation-delay:.44s}.r4{animation-delay:.52s}.r5{animation-delay:.6s}
        @keyframes feed{from{clip-path:inset(0 0 100% 0 round 14px);transform:translateY(14px)} to{clip-path:inset(0 0 0 0 round 14px);transform:none} }
        @keyframes rise{from{opacity:0;transform:translateY(6px)} to{opacity:1;transform:none} }
        @keyframes draw{to{stroke-dashoffset:0} }

        @media (max-width:360px){.whole{font-size:38px}.top,.amount,.pay{padding-left:18px;padding-right:18px}.meta{margin-left:18px;margin-right:18px} }
        @media (prefers-reduced-motion:reduce){
          .slip,.r1,.r2,.r3,.r4,.r5{animation:none}
          .check circle,.check path{animation:none;stroke-dashoffset:0}
          .opt,.arr{transition:none}.opt:hover .arr{transform:none}
        }
        """;
}
