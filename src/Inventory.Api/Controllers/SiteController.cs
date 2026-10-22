using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.AppsEngine;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using static Inventory.Api.Infrastructure.SiteHtml;

namespace Inventory.Api.Controllers;

/// <summary>
/// The public side of the apps: a business's website, blog, events, courses, forum, careers, surveys and signing links, as plain HTML at /api/site/{company}/….
/// Anyone can open these, so nothing here shows anything that hasn't been published, everything a visitor sends is length-limited and escaped when shown, and
/// forum posts wait for approval.
/// </summary>
[ApiController, Route("api/site/{company}"), AllowAnonymous, EnableRateLimiting(Policies.WebChatRateLimit)]
public class SiteController(CompanyRegistry registry, CompanyScopes scopes) : ControllerBase
{
    private sealed record Ctx(IServiceProvider Sp, IBusinessDbContext Db, ICompanyContext Co, AppAccess Access, DateOnly Today, DateTime Now, SiteInfo Site, string Key)
    {
        public string Url(string path) => $"/api/site/{Key}{path}";
    }

    private IActionResult Html(string html) => Content(html, "text/html; charset=utf-8");
    private IActionResult Missing(Ctx c, string what = "That page isn't here.") => new ContentResult { StatusCode = 404, ContentType = "text/html; charset=utf-8", Content = Page(c.Site, "Not found", $"<div class=\"card\"><h1>Not found</h1><p>{H(what)}</p><a class=\"btn alt\" href=\"{c.Url("")}\">Home</a></div>") };
    private static string Trunc(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
    private bool Bot() => Request.HasFormContentType && !string.IsNullOrWhiteSpace(Request.Form["website"]);
    private string Form(string key, int max) => Trunc(Request.Form[key], max);

    private async Task<IActionResult> Run(string company, Func<Ctx, Task<IActionResult>> work)
    {
        if (registry.Find(company) is null) return NotFound();
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        Response.Headers["X-Content-Type-Options"] = "nosniff"; Response.Headers["Referrer-Policy"] = "same-origin";
        return await scopes.RunAsync(company, async sp =>
        {
            var db = sp.GetRequiredService<IBusinessDbContext>(); var co = sp.GetRequiredService<ICompanyContext>(); var clock = sp.GetRequiredService<IClock>();
            var access = sp.GetRequiredService<AppAccess>().For(co.HasNewFeatures, co.HasMarketing);
            return await work(new Ctx(sp, db, co, access, clock.BusinessToday, clock.UtcNow, await LoadSiteAsync(db, co, access, clock.BusinessToday, company), company));
        });
    }

    private static async Task<SiteInfo> LoadSiteAsync(IBusinessDbContext db, ICompanyContext co, AppAccess access, DateOnly today, string key)
    {
        var settings = await db.AppRecords.AsNoTracking().Where(r => r.App == "website" && r.Kind == "site" && r.Status == "Published").FirstOrDefaultAsync();
        var d = settings is null ? [] : Dx.Parse(settings.Data);
        var nav = new List<NavLink> { new("Home", "") };
        if (access.IsEnabled("website"))
        {
            if (d.Bool("showCatalogue")) nav.Add(new("Products", "/products"));
            foreach (var p in (await db.AppRecords.AsNoTracking().Where(r => r.App == "website" && r.Kind == "page" && r.Status == "Published").ToListAsync()).Select(r => (r, d: Dx.Parse(r.Data))).Where(x => x.d.Bool("inMenu")).OrderBy(x => x.d.Int("order") ?? 10).ThenBy(x => x.r.Title))
                nav.Add(new(p.r.Title, "/p/" + p.r.Code));
        }
        if (access.IsEnabled("blog") && await db.AppRecords.AsNoTracking().AnyAsync(r => r.App == "blog" && r.Status == "Published")) nav.Add(new("Blog", "/blog"));
        if (access.IsEnabled("events") && await db.AppRecords.AsNoTracking().AnyAsync(r => r.App == "events" && r.Kind == "event" && r.Status == "Published" && r.Date >= today)) nav.Add(new("Events", "/events"));
        if (access.IsEnabled("elearning") && await db.AppRecords.AsNoTracking().AnyAsync(r => r.App == "elearning" && r.Kind == "course" && r.Status == "Published")) nav.Add(new("Courses", "/courses"));
        if (access.IsEnabled("forum")) nav.Add(new("Forum", "/forum"));
        if (access.IsEnabled("recruitment") && await db.AppRecords.AsNoTracking().AnyAsync(r => r.App == "recruitment" && r.Kind == "job" && r.Status == "Open")) nav.Add(new("Careers", "/careers"));
        return new SiteInfo(key, settings?.Title ?? co.LegalName, d.Str("tagline") ?? "", d.Str("accent") is { Length: > 0 } a ? a : "#0f1e3d", d.Str("phone"), d.Str("email"), d.Str("address"), d.Str("intro"), d.Bool("showCatalogue"), nav);
    }

    private static AppRecord NewRecord(Ctx c, string app, string kind, int? parent, string title, string status, JsonObject data, string? code = null, DateOnly? date = null, decimal? amount = null) =>
        new() { App = app, Kind = kind, ParentId = parent, Title = Trunc(title, 200), Status = status, Code = code, Date = date, Amount = amount, Data = data.ToJsonString(), CreatedByUserId = 0, CreatedByName = "Public site", CreatedAt = c.Now, UpdatedAt = c.Now };

    private static string Done(string? flag, string text) => flag == "1" ? Notice(text) : "";

    // ================================================================ home, pages, products

    [HttpGet("")]
    public Task<IActionResult> Home(string company) => Run(company, async c =>
    {
        var sb = new StringBuilder($"<div class=\"card\"><h1>{H(c.Site.Name)}</h1>");
        if (c.Site.Tagline.Length > 0) sb.Append($"<p class=\"mut\">{H(c.Site.Tagline)}</p>");
        sb.Append(Rich(c.Site.Intro));
        if (!string.IsNullOrWhiteSpace(c.Site.Address)) sb.Append($"<p class=\"mut\">{H(c.Site.Address)}</p>");
        sb.Append("</div>");
        if (c.Access.IsEnabled("website") && c.Site.ShowCatalogue) sb.Append(await ProductGridAsync(c, 8, "Some of what we sell"));
        return Html(Page(c.Site, "Home", sb.ToString(), ""));
    });

    [HttpGet("p/{slug}")]
    public Task<IActionResult> PageBySlug(string company, string slug) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("website")) return Missing(c);
        var p = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "website" && r.Kind == "page" && r.Code == slug && r.Status == "Published");
        return p is null ? Missing(c) : Html(Page(c.Site, p.Title, $"<div class=\"card\"><h1>{H(p.Title)}</h1>{Rich(Dx.Parse(p.Data).Str("body"))}</div>", "/p/" + slug));
    });

    [HttpGet("products")]
    public Task<IActionResult> Products(string company) => Run(company, async c =>
        !c.Access.IsEnabled("website") || !c.Site.ShowCatalogue ? Missing(c) : Html(Page(c.Site, "Products", $"<h1>Products</h1>{await ProductGridAsync(c, 200, null)}", "/products")));

    private static async Task<string> ProductGridAsync(Ctx c, int take, string? heading)
    {
        var items = await c.Db.Products.AsNoTracking().Where(p => p.IsActive && p.PriceRetail > 0).OrderBy(p => p.Name).Take(take).ToListAsync();
        var ids = items.Select(p => p.Id).ToList();
        var stock = await c.Db.StockBatches.AsNoTracking().Where(b => ids.Contains(b.ProductId)).GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.QuantityOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        var sb = new StringBuilder(heading is null ? "" : $"<h2>{H(heading)}</h2>").Append("<div class=\"grid\">");
        foreach (var p in items)
            sb.Append($"<div class=\"card\"><b>{H(p.Name)}</b><br><span class=\"mut\">per {H(p.Unit)}</span><p><b>₦{p.PriceRetail.ToString("N0", CultureInfo.InvariantCulture)}</b><br><span class=\"{(stock.GetValueOrDefault(p.Id) > 0 ? "tick" : "mut")}\">{(stock.GetValueOrDefault(p.Id) > 0 ? "In stock" : "Out of stock")}</span></p></div>");
        return sb.Append(items.Count == 0 ? "<p class=\"mut\">Nothing to show yet.</p>" : "").Append("</div>").ToString();
    }

    // ================================================================ blog

    [HttpGet("blog")]
    public Task<IActionResult> Blog(string company) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("blog")) return Missing(c);
        var posts = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "blog" && r.Status == "Published" && r.Date <= c.Today).OrderByDescending(r => r.Date).Take(50).ToListAsync();
        var sb = new StringBuilder("<h1>Blog</h1>");
        foreach (var p in posts) sb.Append($"<div class=\"card\"><h2 style=\"margin-top:0\"><a href=\"{c.Url("/blog/" + p.Code)}\">{H(p.Title)}</a></h2><p class=\"mut\">{p.Date:dd MMM yyyy}{(Dx.Parse(p.Data).Str("author") is { Length: > 0 } a ? " · " + H(a) : "")}</p><p>{H(Dx.Parse(p.Data).Str("summary"))}</p></div>");
        if (posts.Count == 0) sb.Append("<p class=\"mut\">No posts yet.</p>");
        return Html(Page(c.Site, "Blog", sb.ToString(), "/blog"));
    });

    [HttpGet("blog/{slug}")]
    public Task<IActionResult> BlogPost(string company, string slug) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("blog")) return Missing(c);
        var p = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "blog" && r.Code == slug && r.Status == "Published" && r.Date <= c.Today);
        if (p is null) return Missing(c);
        var d = Dx.Parse(p.Data);
        return Html(Page(c.Site, p.Title, $"<div class=\"card\"><h1>{H(p.Title)}</h1><p class=\"mut\">{p.Date:dd MMMM yyyy}{(d.Str("author") is { Length: > 0 } a ? " · " + H(a) : "")}</p>{Rich(d.Str("body"))}</div><a href=\"{c.Url("/blog")}\">← All posts</a>", "/blog"));
    });

    // ================================================================ events

    [HttpGet("events")]
    public Task<IActionResult> Events(string company) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("events")) return Missing(c);
        var evs = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "events" && r.Kind == "event" && r.Status == "Published" && r.Date >= c.Today).OrderBy(r => r.Date).Take(50).ToListAsync();
        var sb = new StringBuilder("<h1>Events</h1>");
        foreach (var e in evs) { var d = Dx.Parse(e.Data); sb.Append($"<div class=\"card\"><h2 style=\"margin-top:0\"><a href=\"{c.Url("/events/" + e.Code)}\">{H(e.Title)}</a></h2><p class=\"mut\">{e.Date:dddd dd MMMM yyyy}{(d.Str("time") is { Length: > 0 } t ? " · " + H(t) : "")}{(d.Str("venue") is { Length: > 0 } v ? " · " + H(v) : "")}</p></div>"); }
        if (evs.Count == 0) sb.Append("<p class=\"mut\">No upcoming events.</p>");
        return Html(Page(c.Site, "Events", sb.ToString(), "/events"));
    });

    [HttpGet("events/{slug}")]
    public Task<IActionResult> EventPage(string company, string slug, [FromQuery] string? registered) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("events")) return Missing(c);
        var e = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "events" && r.Kind == "event" && r.Code == slug && r.Status == "Published");
        if (e is null) return Missing(c);
        var d = Dx.Parse(e.Data);
        var price = d.Num("price") ?? 0;
        var sb = new StringBuilder($"<div class=\"card\"><h1>{H(e.Title)}</h1><p class=\"mut\">{e.Date:dddd dd MMMM yyyy}{(d.Str("time") is { Length: > 0 } t ? " · " + H(t) : "")}{(d.Str("venue") is { Length: > 0 } v ? " · " + H(v) : "")}</p>{Rich(d.Str("description"))}<p><b>{(price > 0 ? "₦" + price.ToString("N0", CultureInfo.InvariantCulture) + " a ticket" : "Free")}</b></p></div>");
        sb.Append(Done(registered, "You're registered. We'll see you there."));
        if (e.Date >= c.Today)
            sb.Append($"<form class=\"card\" method=\"post\" action=\"{c.Url("/events/" + e.Code + "/register")}\"><h2 style=\"margin-top:0\">Register</h2>{Honeypot}<label>Your name<input name=\"name\" required maxlength=\"120\"></label><label>Email<input name=\"email\" type=\"email\" maxlength=\"150\"></label><label>Phone<input name=\"phone\" type=\"tel\" maxlength=\"30\"></label><label>Tickets<input name=\"tickets\" type=\"number\" min=\"1\" max=\"10\" value=\"1\"></label>{(price > 0 ? "<p class=\"mut\">Payment is arranged with the organiser after you register.</p>" : "")}<button>Register</button></form>");
        return Html(Page(c.Site, e.Title, sb.ToString(), "/events"));
    });

    [HttpPost("events/{slug}/register"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> Register(string company, string slug) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("events")) return Missing(c);
        var e = await c.Db.AppRecords.FirstOrDefaultAsync(r => r.App == "events" && r.Kind == "event" && r.Code == slug && r.Status == "Published" && r.Date >= c.Today);
        if (e is null) return Missing(c);
        if (Bot()) return Redirect(c.Url($"/events/{slug}?registered=1"));
        string name = Form("name", 120), email = Form("email", 150).ToLowerInvariant(), phone = Form("phone", 30);
        int.TryParse(Form("tickets", 3), out var tickets);
        string? problem = name.Length == 0 ? "Please enter your name." : email.Length == 0 && phone.Length == 0 ? "Please give an email or a phone number." : email.Length > 0 && !(email.Contains('@') && email.Contains('.')) ? "That email doesn't look right." : null;
        if (problem is null && email.Length > 0 && (await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "events" && r.Kind == "registration" && r.ParentId == e.Id && r.Status != "Cancelled").Select(r => r.Data).ToListAsync()).Any(x => Dx.Parse(x).Str("email") == email))
            problem = "That email is already registered for this event.";
        var reg = NewRecord(c, "events", "registration", e.Id, name, "Registered", new JsonObject { ["email"] = email.Length == 0 ? null : email, ["phone"] = phone.Length == 0 ? null : phone, ["tickets"] = tickets, ["paid"] = false, ["source"] = "Website" }, null, e.Date,
            AppHelpers.Round(Math.Max(tickets, 1) * (Dx.Parse(e.Data).Num("price") ?? 0)));
        try { if (problem is null) await EventsHook.CheckAsync(c.Db, reg, tickets, HttpContext.RequestAborted); }
        catch (BusinessRuleException ex) { problem = ex.Message; }
        if (problem is not null) return Html(Page(c.Site, e.Title, $"{Notice(problem, false)}<p><a href=\"{c.Url("/events/" + slug)}\">← Back</a></p>", "/events"));
        c.Db.AppRecords.Add(reg);
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url($"/events/{slug}?registered=1"));
    });

    // ================================================================ courses

    [HttpGet("courses")]
    public Task<IActionResult> Courses(string company) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("elearning")) return Missing(c);
        var cs = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "elearning" && r.Kind == "course" && r.Status == "Published").OrderBy(r => r.Title).ToListAsync();
        var sb = new StringBuilder("<h1>Courses</h1>");
        foreach (var x in cs) { var d = Dx.Parse(x.Data); var p = d.Num("price") ?? 0; sb.Append($"<div class=\"card\"><h2 style=\"margin-top:0\"><a href=\"{c.Url("/courses/" + x.Code)}\">{H(x.Title)}</a></h2><p>{H(d.Str("description"))}</p><p class=\"mut\">{(p > 0 ? "₦" + p.ToString("N0", CultureInfo.InvariantCulture) : "Free")}</p></div>"); }
        if (cs.Count == 0) sb.Append("<p class=\"mut\">No courses yet.</p>");
        return Html(Page(c.Site, "Courses", sb.ToString(), "/courses"));
    });

    [HttpGet("courses/{slug}")]
    public Task<IActionResult> Course(string company, string slug, [FromQuery] string? exists) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("elearning")) return Missing(c);
        var course = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "elearning" && r.Kind == "course" && r.Code == slug && r.Status == "Published");
        if (course is null) return Missing(c);
        var d = Dx.Parse(course.Data); var price = d.Num("price") ?? 0;
        var lessons = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "elearning" && r.Kind == "lesson" && r.ParentId == course.Id && r.Status == "Published").ToListAsync();
        var sb = new StringBuilder($"<div class=\"card\"><h1>{H(course.Title)}</h1>{Rich(d.Str("description"))}<p class=\"mut\">{lessons.Count} lesson(s) · {(price > 0 ? "₦" + price.ToString("N0", CultureInfo.InvariantCulture) : "Free")}</p></div>");
        sb.Append("<div class=\"card\"><h2 style=\"margin-top:0\">What's inside</h2><ol>").Append(string.Concat(lessons.OrderBy(l => Dx.Parse(l.Data).Int("order") ?? 0).Select(l => $"<li>{H(l.Title)}</li>"))).Append("</ol></div>");
        if (exists == "1") sb.Append(Notice("That email is already enrolled. Use the link you were given, or ask us to send it again.", false));
        sb.Append($"<form class=\"card\" method=\"post\" action=\"{c.Url("/courses/" + slug + "/enroll")}\"><h2 style=\"margin-top:0\">Enrol</h2>{Honeypot}<label>Your name<input name=\"name\" required maxlength=\"120\"></label><label>Email<input name=\"email\" type=\"email\" required maxlength=\"150\"></label>{(price > 0 ? "<p class=\"mut\">The lessons open once your payment has been confirmed.</p>" : "")}<button>Enrol</button></form>");
        return Html(Page(c.Site, course.Title, sb.ToString(), "/courses"));
    });

    [HttpPost("courses/{slug}/enroll"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> Enroll(string company, string slug) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("elearning")) return Missing(c);
        var course = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "elearning" && r.Kind == "course" && r.Code == slug && r.Status == "Published");
        if (course is null) return Missing(c);
        if (Bot()) return Redirect(c.Url("/courses"));
        string name = Form("name", 120), email = Form("email", 150).ToLowerInvariant();
        if (name.Length == 0 || !(email.Contains('@') && email.Contains('.'))) return Html(Page(c.Site, course.Title, $"{Notice("Please enter your name and a valid email.", false)}<p><a href=\"{c.Url("/courses/" + slug)}\">← Back</a></p>", "/courses"));
        if ((await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "elearning" && r.Kind == "enrollment" && r.ParentId == course.Id).Select(r => r.Data).ToListAsync()).Any(x => Dx.Parse(x).Str("email") == email))
            return Redirect(c.Url($"/courses/{slug}?exists=1"));
        var token = Guid.NewGuid().ToString("N");
        c.Db.AppRecords.Add(NewRecord(c, "elearning", "enrollment", course.Id, name, "Active", new JsonObject { ["email"] = email, ["paid"] = false, ["done"] = 0, ["completedIds"] = "" }, token));
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        var link = $"{Request.Scheme}://{Request.Host}{c.Url("/learn/" + token)}";
        var mail = c.Sp.GetRequiredService<IEmailSender>();
        if (mail.IsConfigured)
            try { await mail.SendAsync(new EmailMessage(email, name, $"Your link: {course.Title}", $"<p>Hello {H(name)},</p><p>Here is your private link to <b>{H(course.Title)}</b>:</p><p><a href=\"{H(link)}\">{H(link)}</a></p>", $"Your link to {course.Title}: {link}", [], c.Site.Name, null), HttpContext.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the page below shows the link anyway */ }
        return Html(Page(c.Site, course.Title, $"<div class=\"card\"><h1>You're enrolled</h1><p>This is your private link. Bookmark it: it keeps your progress.</p><p><a class=\"btn\" href=\"{c.Url("/learn/" + token)}\">Open my course</a></p></div>", "/courses"));
    });

    private async Task<(AppRecord? Enr, AppRecord? Course)> StudentAsync(Ctx c, string token)
    {
        var enr = await c.Db.AppRecords.FirstOrDefaultAsync(r => r.App == "elearning" && r.Kind == "enrollment" && r.Code == token);
        var course = enr is null ? null : await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == enr.ParentId);
        return (enr, course);
    }

    private static HashSet<int> DoneIds(JsonObject d) => (d.Str("completedIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var i) ? i : 0).ToHashSet();

    [HttpGet("learn/{token}")]
    public Task<IActionResult> Learn(string company, string token) => Run(company, async c =>
    {
        var (enr, course) = await StudentAsync(c, token);
        if (enr is null || course is null || !c.Access.IsEnabled("elearning")) return Missing(c);
        var d = Dx.Parse(enr.Data); var price = Dx.Parse(course.Data).Num("price") ?? 0;
        if (price > 0 && !d.Bool("paid"))
            return Html(Page(c.Site, course.Title, $"<div class=\"card\"><h1>{H(course.Title)}</h1><p>Hello {H(enr.Title)}. Your lessons open as soon as we confirm your payment of ₦{price.ToString("N0", CultureInfo.InvariantCulture)}.</p>{(string.IsNullOrWhiteSpace(c.Site.Phone) ? "" : $"<p class=\"mut\">Questions? Call {H(c.Site.Phone)}.</p>")}</div>", "/courses"));
        var lessons = (await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "elearning" && r.Kind == "lesson" && r.ParentId == course.Id && r.Status == "Published").ToListAsync()).OrderBy(l => Dx.Parse(l.Data).Int("order") ?? 0).ToList();
        var done = DoneIds(d);
        var sb = new StringBuilder($"<div class=\"card\"><h1>{H(course.Title)}</h1><p class=\"mut\">{done.Count(i => lessons.Any(l => l.Id == i))} of {lessons.Count} lessons done</p><ol>");
        foreach (var l in lessons) sb.Append($"<li><a href=\"{c.Url($"/learn/{token}/lesson/{l.Id}")}\">{H(l.Title)}</a> {(done.Contains(l.Id) ? "<span class=\"tick\">✓</span>" : "")}</li>");
        sb.Append("</ol>");
        if (enr.Status == "Completed") sb.Append($"<p><a class=\"btn\" href=\"{c.Url("/certificate/" + token)}\">View my certificate</a></p>");
        return Html(Page(c.Site, course.Title, sb.Append("</div>").ToString(), "/courses"));
    });

    [HttpGet("learn/{token}/lesson/{id:int}")]
    public Task<IActionResult> LessonPage(string company, string token, int id) => Run(company, async c =>
    {
        var (enr, course) = await StudentAsync(c, token);
        if (enr is null || course is null) return Missing(c);
        var d = Dx.Parse(enr.Data);
        if ((Dx.Parse(course.Data).Num("price") ?? 0) > 0 && !d.Bool("paid")) return Redirect(c.Url("/learn/" + token));
        var lesson = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ParentId == course.Id && r.Kind == "lesson" && r.Status == "Published");
        if (lesson is null) return Missing(c);
        var form = DoneIds(d).Contains(id) ? "<p class=\"tick\">✓ Completed</p>" : $"<form method=\"post\" action=\"{c.Url($"/learn/{token}/complete/{id}")}\"><button>Mark this lesson complete</button></form>";
        return Html(Page(c.Site, lesson.Title, $"<div class=\"card\"><h1>{H(lesson.Title)}</h1>{Rich(Dx.Parse(lesson.Data).Str("body"))}{form}</div><a href=\"{c.Url("/learn/" + token)}\">← All lessons</a>", "/courses"));
    });

    [HttpPost("learn/{token}/complete/{id:int}")]
    public Task<IActionResult> CompleteLesson(string company, string token, int id) => Run(company, async c =>
    {
        var (enr, course) = await StudentAsync(c, token);
        if (enr is null || course is null) return Missing(c);
        var d = Dx.Parse(enr.Data);
        if ((Dx.Parse(course.Data).Num("price") ?? 0) > 0 && !d.Bool("paid")) return Redirect(c.Url("/learn/" + token));
        var lessons = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "elearning" && r.Kind == "lesson" && r.ParentId == course.Id && r.Status == "Published").Select(r => r.Id).ToListAsync();
        if (!lessons.Contains(id)) return Missing(c);
        var done = DoneIds(d); done.Add(id);
        d.Set("completedIds", string.Join(",", done)); d.Set("done", done.Count(lessons.Contains));
        if (lessons.All(done.Contains) && enr.Status == "Active") { enr.Status = "Completed"; d.Set("certificate", $"{enr.Id:D6}"); }
        enr.Data = d.ToJsonString(); enr.UpdatedAt = c.Now;
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url("/learn/" + token));
    });

    [HttpGet("certificate/{token}")]
    public Task<IActionResult> Certificate(string company, string token) => Run(company, async c =>
    {
        var (enr, course) = await StudentAsync(c, token);
        if (enr is null || course is null || enr.Status != "Completed") return Missing(c, "This certificate isn't available.");
        return Html(Page(c.Site, "Certificate", $"<div class=\"card\" style=\"text-align:center;padding:3rem 1.5rem;border:3px double var(--a)\"><p class=\"mut\">CERTIFICATE OF COMPLETION</p><h1>{H(enr.Title)}</h1><p>has completed the course</p><h2>{H(course.Title)}</h2><p class=\"mut\">{enr.UpdatedAt:dd MMMM yyyy} · No. {H(Dx.Parse(enr.Data).Str("certificate"))}<br>{H(c.Site.Name)}</p></div><p class=\"noprint mut\">Use your browser's Print to keep this as a PDF.</p>", "/courses"));
    });

    // ================================================================ forum

    [HttpGet("forum")]
    public Task<IActionResult> Forum(string company, [FromQuery] string? posted) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("forum")) return Missing(c);
        var topics = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "forum" && r.Kind == "topic" && (r.Status == "Open" || r.Status == "Closed")).OrderByDescending(r => r.UpdatedAt).Take(50).ToListAsync();
        var sb = new StringBuilder("<h1>Forum</h1>").Append(Done(posted, "Thank you. Your post will appear once it has been approved."));
        foreach (var t in topics) sb.Append($"<div class=\"card\"><a href=\"{c.Url("/forum/" + t.Id)}\"><b>{H(t.Title)}</b></a><br><span class=\"mut\">{H(Dx.Parse(t.Data).Str("author"))} · {t.CreatedAt:dd MMM yyyy}{(t.Status == "Closed" ? " · closed" : "")}</span></div>");
        if (topics.Count == 0) sb.Append("<p class=\"mut\">No topics yet. Be the first.</p>");
        sb.Append($"<form class=\"card\" method=\"post\" action=\"{c.Url("/forum")}\"><h2 style=\"margin-top:0\">Ask a question</h2>{Honeypot}<label>Your name<input name=\"author\" required maxlength=\"60\"></label><label>Title<input name=\"title\" required maxlength=\"150\"></label><label>Details<textarea name=\"body\" required maxlength=\"2000\"></textarea></label><button>Post</button></form>");
        return Html(Page(c.Site, "Forum", sb.ToString(), "/forum"));
    });

    [HttpPost("forum"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> NewTopic(string company) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("forum")) return Missing(c);
        if (Bot()) return Redirect(c.Url("/forum?posted=1"));
        string author = Form("author", 60), title = Form("title", 150), body = Form("body", 2000);
        if (author.Length == 0 || title.Length == 0 || body.Length == 0) return Html(Page(c.Site, "Forum", $"{Notice("Please fill in every box.", false)}<p><a href=\"{c.Url("/forum")}\">← Back</a></p>", "/forum"));
        c.Db.AppRecords.Add(NewRecord(c, "forum", "topic", null, title, "Pending", new JsonObject { ["author"] = author, ["body"] = body }));
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url("/forum?posted=1"));
    });

    [HttpGet("forum/{id:int}")]
    public Task<IActionResult> Topic(string company, int id, [FromQuery] string? posted) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("forum")) return Missing(c);
        var t = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.App == "forum" && r.Kind == "topic" && (r.Status == "Open" || r.Status == "Closed"));
        if (t is null) return Missing(c);
        var d = Dx.Parse(t.Data);
        var replies = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "forum" && r.Kind == "reply" && r.ParentId == id && r.Status == "Visible").OrderBy(r => r.Id).ToListAsync();
        var sb = new StringBuilder($"<div class=\"card\"><h1>{H(t.Title)}</h1><p class=\"mut\">{H(d.Str("author"))} · {t.CreatedAt:dd MMM yyyy}</p>{Rich(d.Str("body"))}</div>");
        foreach (var r in replies) { var rd = Dx.Parse(r.Data); sb.Append($"<div class=\"card\"><p class=\"mut\">{H(rd.Str("author"))} · {r.CreatedAt:dd MMM yyyy}</p>{Rich(rd.Str("body"))}</div>"); }
        sb.Append(Done(posted, "Thank you. Your reply will appear once it has been approved."));
        sb.Append(t.Status == "Closed" ? "<p class=\"mut\">This topic is closed.</p>"
            : $"<form class=\"card\" method=\"post\" action=\"{c.Url($"/forum/{id}/reply")}\"><h2 style=\"margin-top:0\">Reply</h2>{Honeypot}<label>Your name<input name=\"author\" required maxlength=\"60\"></label><label>Reply<textarea name=\"body\" required maxlength=\"2000\"></textarea></label><button>Post reply</button></form>");
        return Html(Page(c.Site, t.Title, sb.Append($"<p><a href=\"{c.Url("/forum")}\">← All topics</a></p>").ToString(), "/forum"));
    });

    [HttpPost("forum/{id:int}/reply"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> Reply(string company, int id) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("forum")) return Missing(c);
        var t = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.App == "forum" && r.Kind == "topic" && r.Status == "Open");
        if (t is null) return Missing(c);
        if (Bot()) return Redirect(c.Url($"/forum/{id}?posted=1"));
        string author = Form("author", 60), body = Form("body", 2000);
        if (author.Length == 0 || body.Length == 0) return Html(Page(c.Site, "Forum", $"{Notice("Please fill in every box.", false)}<p><a href=\"{c.Url("/forum/" + id)}\">← Back</a></p>", "/forum"));
        c.Db.AppRecords.Add(NewRecord(c, "forum", "reply", id, body.Length > 60 ? body[..60] + "…" : body, "Pending", new JsonObject { ["author"] = author, ["body"] = body }));
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url($"/forum/{id}?posted=1"));
    });

    // ================================================================ careers

    [HttpGet("careers")]
    public Task<IActionResult> Careers(string company, [FromQuery] string? applied) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("recruitment")) return Missing(c);
        var jobs = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "recruitment" && r.Kind == "job" && r.Status == "Open").OrderBy(r => r.Title).ToListAsync();
        var sb = new StringBuilder("<h1>Careers</h1>").Append(Done(applied, "Thank you. We have your application and will be in touch."));
        foreach (var j in jobs)
        {
            var d = Dx.Parse(j.Data);
            sb.Append($"<form class=\"card\" method=\"post\" action=\"{c.Url($"/careers/{j.Id}/apply")}\"><h2 style=\"margin-top:0\">{H(j.Title)}</h2><p class=\"mut\">{H(d.Str("department"))}{(d.Str("location") is { Length: > 0 } l ? " · " + H(l) : "")}</p>{Rich(d.Str("description"))}<h3>Apply</h3>{Honeypot}<label>Your name<input name=\"name\" required maxlength=\"120\"></label><label>Phone<input name=\"phone\" type=\"tel\" required maxlength=\"30\"></label><label>Email<input name=\"email\" type=\"email\" maxlength=\"150\"></label><label>Tell us about yourself<textarea name=\"notes\" maxlength=\"2000\"></textarea></label><button>Apply</button></form>");
        }
        if (jobs.Count == 0) sb.Append("<p class=\"mut\">There are no open jobs right now.</p>");
        return Html(Page(c.Site, "Careers", sb.ToString(), "/careers"));
    });

    [HttpPost("careers/{id:int}/apply"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> Apply(string company, int id) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("recruitment")) return Missing(c);
        if (!await c.Db.AppRecords.AsNoTracking().AnyAsync(r => r.Id == id && r.App == "recruitment" && r.Kind == "job" && r.Status == "Open")) return Missing(c);
        if (Bot()) return Redirect(c.Url("/careers?applied=1"));
        string name = Form("name", 120), phone = Form("phone", 30), email = Form("email", 150);
        if (name.Length == 0 || phone.Length == 0) return Html(Page(c.Site, "Careers", $"{Notice("Please enter your name and phone number.", false)}<p><a href=\"{c.Url("/careers")}\">← Back</a></p>", "/careers"));
        c.Db.AppRecords.Add(NewRecord(c, "recruitment", "applicant", id, name, "Applied", new JsonObject { ["phone"] = phone, ["email"] = email.Length == 0 ? null : email, ["notes"] = Form("notes", 2000), ["source"] = "Website" }));
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url("/careers?applied=1"));
    });

    // ================================================================ surveys

    [HttpGet("survey")]
    public Task<IActionResult> Surveys(string company) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("surveys")) return Missing(c);
        var list = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "surveys" && r.Kind == "survey" && r.Status == "Open").OrderBy(r => r.Title).ToListAsync();
        var sb = new StringBuilder("<h1>Surveys</h1>");
        foreach (var s in list) sb.Append($"<div class=\"card\"><a href=\"{c.Url("/survey/" + s.Code)}\"><b>{H(s.Title)}</b></a></div>");
        if (list.Count == 0) sb.Append("<p class=\"mut\">No surveys are open right now.</p>");
        return Html(Page(c.Site, "Surveys", sb.ToString(), "/survey"));
    });

    [HttpGet("survey/{slug}")]
    public Task<IActionResult> SurveyPage(string company, string slug, [FromQuery] string? thanks) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("surveys")) return Missing(c);
        var s = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "surveys" && r.Kind == "survey" && r.Code == slug && r.Status == "Open");
        if (s is null) return Missing(c, "This survey isn't open.");
        if (thanks == "1") return Html(Page(c.Site, s.Title, $"<div class=\"card\"><h1>Thank you</h1><p>Your answers have been received.</p></div>", "/survey"));
        var d = Dx.Parse(s.Data);
        var sb = new StringBuilder($"<form class=\"card\" method=\"post\" action=\"{c.Url("/survey/" + slug)}\"><h1>{H(s.Title)}</h1>{Rich(d.Str("intro"))}{Honeypot}<label>Your name (optional)<input name=\"who\" maxlength=\"80\"></label>");
        var qs = SurveyQuestions.Parse(d.Str("questions") ?? "");
        for (var i = 0; i < qs.Count; i++)
        {
            var q = qs[i];
            sb.Append($"<label>{i + 1}. {H(q.Text)}</label>");
            sb.Append(q.Type switch
            {
                "rating" => $"<select name=\"q{i}\"><option value=\"\">Choose…</option>{string.Concat(Enumerable.Range(1, 5).Select(n => $"<option>{n}</option>"))}</select><span class=\"mut\">1 = poor, 5 = excellent</span>",
                "yesno" => $"<select name=\"q{i}\"><option value=\"\">Choose…</option><option>Yes</option><option>No</option></select>",
                "choice" => $"<select name=\"q{i}\"><option value=\"\">Choose…</option>{string.Concat(q.Choices.Select(ch => $"<option>{H(ch)}</option>"))}</select>",
                _ => $"<textarea name=\"q{i}\" maxlength=\"500\"></textarea>",
            });
        }
        return Html(Page(c.Site, s.Title, sb.Append("<button>Send my answers</button></form>").ToString(), "/survey"));
    });

    [HttpPost("survey/{slug}"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> SurveySubmit(string company, string slug) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("surveys")) return Missing(c);
        var s = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "surveys" && r.Kind == "survey" && r.Code == slug && r.Status == "Open");
        if (s is null) return Missing(c, "This survey isn't open.");
        if (Bot()) return Redirect(c.Url($"/survey/{slug}?thanks=1"));
        var qs = SurveyQuestions.Parse(Dx.Parse(s.Data).Str("questions") ?? "");
        var answers = new JsonArray();
        for (var i = 0; i < qs.Count; i++)
        {
            var a = Form($"q{i}", 500);
            // Only answers the question allows are kept; anything else is dropped.
            var ok = qs[i].Type switch { "rating" => a is "1" or "2" or "3" or "4" or "5", "yesno" => a is "Yes" or "No", "choice" => qs[i].Choices.Contains(a), _ => true };
            answers.Add(ok ? a : "");
        }
        if (answers.All(a => string.IsNullOrEmpty(a?.ToString()))) return Html(Page(c.Site, s.Title, $"{Notice("Please answer at least one question.", false)}<p><a href=\"{c.Url("/survey/" + slug)}\">← Back</a></p>", "/survey"));
        var who = Form("who", 80);
        c.Db.AppRecords.Add(NewRecord(c, "surveys", "response", s.Id, who.Length == 0 ? "Anonymous" : who, "Received", new JsonObject { ["answers"] = answers }));
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url($"/survey/{slug}?thanks=1"));
    });

    // ================================================================ signing

    [HttpGet("sign/{token}")]
    public Task<IActionResult> SignPage(string company, string token) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("sign")) return Missing(c);
        var doc = await c.Db.AppRecords.AsNoTracking().FirstOrDefaultAsync(r => r.App == "sign" && r.Kind == "document" && r.Code == token);
        if (doc is null || doc.Status == "Draft") return Missing(c, "This document isn't available.");
        var d = Dx.Parse(doc.Data);
        var sb = new StringBuilder($"<div class=\"card\"><h1>{H(doc.Title)}</h1>{Rich(d.Str("body"))}</div>");
        sb.Append(doc.Status switch
        {
            "Signed" => Notice($"Signed by {d.Str("signedName")} on {d.Str("signedAt")?[..10]}. Thank you."),
            "Declined" => Notice("This document was declined.", false),
            "Expired" => Notice("This signing link has expired. Ask the sender for a new one.", false),
            _ => $"<form class=\"card\" method=\"post\" action=\"{c.Url("/sign/" + token)}\"><h2 style=\"margin-top:0\">Sign</h2><p class=\"mut\">Type your full name to sign. {H(d.Str("signerName"))} is expected to sign.</p>{Honeypot}<label>Full name<input name=\"name\" required maxlength=\"120\"></label><label style=\"font-weight:400\"><input type=\"checkbox\" name=\"agree\" value=\"yes\" style=\"width:auto\"> I have read this document and I agree to it.</label><button name=\"choice\" value=\"sign\">Sign</button> <button name=\"choice\" value=\"decline\" class=\"btn alt\" style=\"background:#fff;color:var(--a);border:1px solid var(--a)\">Decline</button></form>",
        });
        return Html(Page(c.Site, doc.Title, sb.ToString(), ""));
    });

    [HttpPost("sign/{token}"), Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> SignSubmit(string company, string token) => Run(company, async c =>
    {
        if (!c.Access.IsEnabled("sign")) return Missing(c);
        var doc = await c.Db.AppRecords.FirstOrDefaultAsync(r => r.App == "sign" && r.Kind == "document" && r.Code == token);
        if (doc is null || doc.Status != "Sent") return Missing(c, "This document can't be signed any more.");
        if (Bot()) return Redirect(c.Url("/sign/" + token));
        var d = Dx.Parse(doc.Data);
        if (Form("choice", 10) == "decline") { doc.Status = "Declined"; }
        else
        {
            var name = Form("name", 120);
            if (name.Length == 0 || Form("agree", 5) != "yes") return Html(Page(c.Site, doc.Title, $"{Notice("Please type your name and tick the box to sign.", false)}<p><a href=\"{c.Url("/sign/" + token)}\">← Back</a></p>"));
            doc.Status = "Signed"; d.Set("signedName", name); d.Set("signedAt", c.Now.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)); d.Set("signedFrom", HttpContext.Connection.RemoteIpAddress?.ToString());
        }
        doc.Data = d.ToJsonString(); doc.UpdatedAt = c.Now;
        c.Db.AuditLogs.Add(new AuditLog { UserId = 0, UserName = "Public signing link", Action = doc.Status == "Signed" ? "SIGN_SIGNED" : "SIGN_DECLINED", Entity = "AppRecord", EntityId = doc.Id.ToString(), At = c.Now, Detail = doc.Title });
        await c.Db.SaveChangesAsync(HttpContext.RequestAborted);
        return Redirect(c.Url("/sign/" + token));
    });
}
