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
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Naira(decimal v) => "₦" + v.ToString("N2", CultureInfo.InvariantCulture);
    private static (string Accent, string Tint) Colours(string companyKey) => companyKey == "candid" ? ("#6A2C5B", "#F5EEF3") : ("#1F6B4F", "#EEF5F1");

    private static (string Name, string Blurb) ProviderText(string p) => p switch
    {
        PaymentProviders.Paystack => ("Paystack", "Card, bank transfer or USSD"),
        PaymentProviders.AlatPay => ("AlatPay", "Card or bank transfer · by Wema Bank"),
        _ => (p, "Pay online"),
    };

    private static string Render(PayPageInfo i, string token, string companyKey, string? error)
    {
        var (accent, tint) = Colours(companyKey);
        var body = new System.Text.StringBuilder();
        body.Append($"<p class=\"eyebrow\">{E(i.Business)}</p><h1>Pay online</h1>");
        body.Append($"<div class=\"doc\"><div><span class=\"k\">{E(i.DocKind)}</span><span class=\"v mono\">{E(i.DocNumber)}</span></div>" +
                    $"<div><span class=\"k\">Customer</span><span class=\"v\">{E(i.Customer)}</span></div></div>");
        if (error is not null) body.Append($"<p class=\"note bad\" role=\"alert\">{E(error)}</p>");

        if (i.Closed is not null)
        {
            body.Append($"<div class=\"done\"><span class=\"tick\" aria-hidden=\"true\">✓</span><p>{E(i.Closed)}</p></div>");
        }
        else
        {
            body.Append($"<div class=\"amount\"><span class=\"k\">Amount due</span><span class=\"big\">{Naira(i.Amount)}</span></div>");
            if (i.Awaiting) body.Append("<p class=\"hint\">Already paid? It can take a minute to show here — refresh this page before paying again.</p>");
            if (i.Providers.Count == 0) body.Append("<p class=\"note bad\">Online payment isn't available right now. Please pay by bank transfer using the details on your receipt.</p>");
            else
            {
                body.Append("<p class=\"choose\">Choose how you'd like to pay</p><div class=\"options\">");
                foreach (var p in i.Providers)
                {
                    var (name, blurb) = ProviderText(p);
                    body.Append($"<a class=\"opt\" href=\"/pay/{E(token)}/{E(p)}\"><span class=\"on\">Pay with {E(name)}</span><span class=\"sub\">{E(blurb)}</span><span class=\"arrow\" aria-hidden=\"true\">›</span></a>");
                }
                body.Append("</div><p class=\"fine\">You'll finish on the payment provider's secure page, then come back here. We never see your card details.</p>");
            }
        }
        return Shell($"Pay {E(i.DocKind.ToLowerInvariant())} {E(i.DocNumber)} · {E(i.Business)}", accent, tint, body.ToString());
    }

    private static string NotFoundPage() => Shell("Payment link not found", "#1F6B4F", "#EEF5F1",
        "<h1>Link not recognised</h1><p class=\"note\">This payment link isn't valid. Please use the PAY ONLINE button on your latest receipt or quotation, or contact us.</p>");

    private static string Shell(string title, string accent, string tint, string body) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex,nofollow"><title>{{title}}</title>
        <style>
          :root{--accent:{{accent}};--tint:{{tint}};--ink:#16202A;--muted:#56636C;--line:#D3DAD5;--bad:#C4372C}
          *{box-sizing:border-box}html,body{margin:0}
          body{min-height:100vh;display:grid;place-items:center;padding:24px 16px;background:linear-gradient(160deg,var(--tint),#fff 60%);
               font:16px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;color:var(--ink)}
          main{width:100%;max-width:440px;background:#fff;border:1px solid var(--line);border-radius:18px;padding:28px 24px 22px;box-shadow:0 22px 60px -30px rgba(22,32,42,.45)}
          .eyebrow{margin:0 0 4px;font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--accent)}
          h1{margin:0 0 18px;font-size:26px;line-height:1.15}
          .doc{display:grid;gap:6px;padding:12px 14px;border-radius:12px;background:var(--tint)}
          .doc div,.amount{display:flex;justify-content:space-between;gap:12px;align-items:baseline}
          .k{color:var(--muted);font-size:13px}.v{font-weight:600;text-align:right;overflow-wrap:anywhere}.mono{font-family:ui-monospace,Consolas,monospace;font-size:14px}
          .amount{margin:18px 2px 6px}.big{font-size:32px;font-weight:800;letter-spacing:-.01em;color:var(--ink)}
          .choose{margin:16px 0 8px;font-weight:700}
          .options{display:grid;gap:10px}
          .opt{position:relative;display:grid;padding:14px 44px 14px 16px;border:1.5px solid var(--line);border-radius:14px;text-decoration:none;color:var(--ink);transition:border-color .15s,box-shadow .15s,transform .15s}
          .opt:hover,.opt:focus-visible{border-color:var(--accent);box-shadow:0 8px 24px -14px var(--accent);transform:translateY(-1px);outline:none}
          .opt:first-child{background:var(--accent);border-color:var(--accent);color:#fff}.opt:first-child .sub{color:rgba(255,255,255,.85)}
          .on{font-weight:700;font-size:17px}.sub{font-size:13px;color:var(--muted)}
          .arrow{position:absolute;right:16px;top:50%;transform:translateY(-50%);font-size:26px;line-height:1}
          .note{margin:14px 0 0;padding:10px 12px;border-radius:10px;background:#FBF6E6;font-size:14px}.note.bad{background:#FBEBEA;color:var(--bad);font-weight:600}
          .done{display:flex;gap:12px;align-items:center;margin-top:18px;padding:14px;border-radius:12px;background:var(--tint)}
          .done p{margin:0;font-weight:600}.tick{flex:none;display:grid;place-items:center;width:34px;height:34px;border-radius:50%;background:var(--accent);color:#fff;font-weight:800}
          .fine{margin:14px 2px 0;font-size:12px;color:var(--muted)}
          .hint{margin:4px 2px 0;font-size:13px;color:var(--muted)}
          .mono{word-break:break-all}
          @media (prefers-reduced-motion:reduce){.opt{transition:none}.opt:hover{transform:none} }
        </style></head><body><main>{{body}}</main></body></html>
        """;
}
