using System.Net;
using System.Text;
using Inventory.Application.Abstractions;

namespace Inventory.Api.Infrastructure;

public sealed record NavLink(string Label, string Path);

/// <summary>What every public page needs to draw itself: the business, its look, and which sections to link to.</summary>
public sealed record SiteInfo(string Key, string Name, string Tagline, string Accent, string? Phone, string? Email, string? Address, string? Intro, bool ShowCatalogue, IReadOnlyList<NavLink> Nav);

/// <summary>
/// Plain, dependency-free HTML for the public site. Every value that came from a person or from the business's own records goes through <see cref="H"/>;
/// there is no script on any page, and the pages are sent with a policy that forbids it.
/// </summary>
public static class SiteHtml
{
    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>Plain text to paragraphs: a blank line starts a paragraph, "# " and "## " start headings, single line breaks stay.</summary>
    public static string Rich(string? text)
    {
        var sb = new StringBuilder();
        foreach (var block in (text ?? "").Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (block.StartsWith("## ")) sb.Append($"<h3>{H(block[3..])}</h3>");
            else if (block.StartsWith("# ")) sb.Append($"<h2>{H(block[2..])}</h2>");
            else sb.Append($"<p>{H(block).Replace("\n", "<br>")}</p>");
        }
        return sb.ToString();
    }

    public static string Page(SiteInfo s, string title, string body, string? current = null)
    {
        var accent = System.Text.RegularExpressions.Regex.IsMatch(s.Accent, "^#[0-9a-fA-F]{6}$") ? s.Accent : "#0f1e3d";
        var nav = string.Join("", s.Nav.Select(n => $"<a href=\"/api/site/{H(s.Key)}{H(n.Path)}\"{(n.Path == current ? " class=\"on\"" : "")}>{H(n.Label)}</a>"));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{H(title)}} · {{H(s.Name)}}</title>
<style>
:root{--a:{{accent}};--ink:#1b2333;--mut:#5b6577;--line:#dfe3ec;--bg:#f6f7fa}
*{box-sizing:border-box}body{margin:0;font:16px/1.6 system-ui,-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:var(--ink);background:var(--bg)}
header{background:var(--a);color:#fff}.w{max-width:860px;margin:0 auto;padding:0 1.1rem}header .w{display:flex;flex-wrap:wrap;align-items:center;gap:.4rem 1.4rem;padding:.9rem 1.1rem}
header b{font-size:1.2rem;margin-right:auto}header a{color:#fff;text-decoration:none;opacity:.85;padding:.2rem 0}header a.on,header a:hover{opacity:1;text-decoration:underline}
main{padding:1.6rem 0 3rem}.card{background:#fff;border:1px solid var(--line);border-radius:12px;padding:1.2rem 1.3rem;margin:0 0 1rem}
h1{font-size:1.8rem;line-height:1.2;margin:.2rem 0 .8rem}h2{font-size:1.3rem;margin:1.2rem 0 .4rem}h3{font-size:1.1rem}p{margin:.5rem 0 .9rem}.mut{color:var(--mut);font-size:.9rem}
a{color:var(--a)}label{display:block;font-weight:600;margin:.8rem 0 .25rem}input,textarea,select{width:100%;font:inherit;padding:.55rem .65rem;border:1px solid var(--line);border-radius:8px;background:#fff}
textarea{min-height:6rem}button,.btn{display:inline-block;margin-top:1rem;font:inherit;font-weight:600;background:var(--a);color:#fff;border:0;border-radius:8px;padding:.6rem 1.2rem;cursor:pointer;text-decoration:none}
.btn.alt{background:#fff;color:var(--a);border:1px solid var(--a)}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:.8rem}.hp{position:absolute;left:-9999px}
.ok{background:#e6f5ee;border:1px solid #9fd5b9;padding:.8rem 1rem;border-radius:8px}.bad{background:#fdecea;border:1px solid #f1aaa3;padding:.8rem 1rem;border-radius:8px}
.tick{color:#0f7a4d;font-weight:700}footer{color:var(--mut);font-size:.85rem;text-align:center;padding:1.5rem 1rem}
@media print{header,footer,.noprint{display:none}body{background:#fff}.card{border:0} }
</style></head><body><header><div class="w"><b>{{H(s.Name)}}</b>{{nav}}</div></header><main><div class="w">{{body}}</div></main>
<footer>{{H(s.Name)}}{{(string.IsNullOrWhiteSpace(s.Phone) ? "" : " · " + H(s.Phone))}}{{(string.IsNullOrWhiteSpace(s.Email) ? "" : " · " + H(s.Email))}}</footer></body></html>
""";
    }

    public static string Notice(string text, bool good = true) => $"<div class=\"{(good ? "ok" : "bad")}\" role=\"{(good ? "status" : "alert")}\">{H(text)}</div>";

    /// <summary>A hidden field a person never fills in; a bot that does is ignored.</summary>
    public const string Honeypot = "<div class=\"hp\" aria-hidden=\"true\"><label>Leave this empty<input name=\"website\" tabindex=\"-1\" autocomplete=\"off\"></label></div>";
}
