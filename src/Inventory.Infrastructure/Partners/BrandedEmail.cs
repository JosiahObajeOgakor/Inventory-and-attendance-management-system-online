using System.Net;
using System.Text;
using Inventory.Application.Abstractions;
using Inventory.Application.Email;
using Inventory.Application.Partners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Inventory.Infrastructure.Partners;

/// <summary>
/// Wraps a message in the store's own email look. The logo comes from Company &amp; documents (shrunk to a few KB so it never weighs the email down) and
/// travels inside the email (no "load images" prompt). When an email concerns both stores, Candid Purrfect's logo leads, as the owner asked.
/// </summary>
public sealed class BrandedEmail(IEmailSender email, ICompanyScopes stores, IMemoryCache cache) : IBrandedEmail
{
    private const string Candid = "candid";
    private static readonly Dictionary<string, (string Accent, string Ink)> Colours = new()
    {
        ["candid"] = ("#c2410c", "#1f2937"),
        ["chewypets"] = ("#6d28d9", "#170b22"),
    };

    public bool IsConfigured => email.IsConfigured;

    public async Task SendAsync(BrandedMail m, CancellationToken ct)
    {
        var key = m.Stores.Contains(Candid) ? Candid : m.Stores.FirstOrDefault() ?? stores.TradeStores[0];
        var (logo, contact) = await BrandAsync(key, ct);
        var name = stores.DisplayName(key);
        var (accent, ink) = Colours.GetValueOrDefault(key, ("#6d28d9", "#170b22"));
        var html = Html(m, name, contact, accent, ink, logo is not null);
        await email.SendAsync(new EmailMessage(m.To, m.ToName, m.Subject, html, Text(m, name, contact), [], name, null, logo), ct);
    }

    private async Task<(byte[]? Logo, string Contact)> BrandAsync(string key, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"brand-mail:{key}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return await stores.InAsync(key, async sp =>
            {
                var db = sp.GetRequiredService<IBusinessDbContext>();
                var asset = await db.CompanyAssets.AsNoTracking().Where(a => a.Kind == "logo").Select(a => a.Data).FirstOrDefaultAsync(ct);
                var p = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
                var contact = string.Join(" · ", new[] { p?.Address, p?.Phone, p?.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return (asset is null ? null : Shrink(asset), contact);
            });
        });

    /// <summary>At most 320px wide, as PNG: a 900 KB logo becomes a few KB.</summary>
    public static byte[]? Shrink(byte[] data)
    {
        try
        {
            using var src = SKBitmap.Decode(data);
            if (src is null) return null;
            var scale = Math.Min(1f, 320f / src.Width);
            using var dst = src.Resize(new SKImageInfo(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var img = SKImage.FromBitmap(dst ?? src);
            return img.Encode(SKEncodedImageFormat.Png, 90).ToArray();
        }
        catch { return null; }
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Html(BrandedMail m, string name, string contact, string accent, string ink, bool logo)
    {
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html><body style=\"margin:0;padding:0;background:#f4f2f7\"><div style=\"display:none;max-height:0;overflow:hidden\">{E(m.Paragraphs.FirstOrDefault())}</div>");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f2f7;padding:24px 12px\"><tr><td align=\"center\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:16px;overflow:hidden;font-family:Segoe UI,Helvetica,Arial,sans-serif\">");
        sb.Append($"<tr><td style=\"padding:24px 28px 18px;text-align:left\">{(logo ? $"<img src=\"cid:logo\" alt=\"{E(name)}\" style=\"max-height:56px;max-width:220px;display:block\">" : $"<strong style=\"font-size:20px;color:{ink}\">{E(name)}</strong>")}</td></tr>");
        sb.Append($"<tr><td style=\"height:4px;background:{accent};line-height:4px;font-size:0\">&nbsp;</td></tr>");
        sb.Append($"<tr><td style=\"padding:28px 28px 8px\"><h1 style=\"margin:0 0 14px;font-size:22px;line-height:1.3;color:{ink}\">{E(m.Heading)}</h1>");
        foreach (var p in m.Paragraphs) sb.Append($"<p style=\"margin:0 0 14px;font-size:15px;line-height:1.6;color:#374151\">{E(p)}</p>");
        if (m.Code is { Length: > 0 })
            sb.Append($"<div style=\"margin:8px 0 18px;padding:16px;border-radius:12px;background:#f6f3ff;border:1px solid #e4dcfb;text-align:center;font:700 32px/1 Consolas,Menlo,monospace;letter-spacing:10px;color:{ink}\">{E(m.Code)}</div>");
        if (m.ButtonText is { Length: > 0 } && m.ButtonUrl is { Length: > 0 })
            sb.Append($"<p style=\"margin:8px 0 20px\"><a href=\"{E(m.ButtonUrl)}\" style=\"display:inline-block;padding:13px 24px;border-radius:999px;background:{accent};color:#ffffff;text-decoration:none;font-weight:700;font-size:15px\">{E(m.ButtonText)}</a></p>");
        if (m.Footnote is { Length: > 0 }) sb.Append($"<p style=\"margin:0 0 8px;font-size:13px;line-height:1.5;color:#6b7280\">{E(m.Footnote)}</p>");
        sb.Append("</td></tr>");
        sb.Append($"<tr><td style=\"padding:18px 28px 24px;border-top:1px solid #eee;font-size:12px;line-height:1.6;color:#6b7280\"><strong style=\"color:{ink}\">{E(name)}</strong>{(contact.Length > 0 ? "<br>" + E(contact) : "")}</td></tr>");
        sb.Append("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    private static string Text(BrandedMail m, string name, string contact)
    {
        var sb = new StringBuilder().Append(m.Heading).Append("\n\n");
        foreach (var p in m.Paragraphs) sb.Append(p).Append("\n\n");
        if (m.Code is { Length: > 0 }) sb.Append("Your code: ").Append(m.Code).Append("\n\n");
        if (m.ButtonUrl is { Length: > 0 }) sb.Append(m.ButtonText).Append(": ").Append(m.ButtonUrl).Append("\n\n");
        if (m.Footnote is { Length: > 0 }) sb.Append(m.Footnote).Append("\n\n");
        return sb.Append(name).Append(contact.Length > 0 ? "\n" + contact : "").ToString();
    }
}
