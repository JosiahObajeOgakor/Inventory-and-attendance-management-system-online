using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

/// <summary>Social Marketing: a planner with share links (no automatic posting: that needs each network's approval of an app).</summary>
public sealed class SocialHook : IAppHook
{
    public string App => "social-marketing";

    public Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct)
    {
        var d = Dx.Parse(r.Data);
        var content = d.Str("content") ?? "";
        var q = Uri.EscapeDataString(content);
        var platform = d.Str("platform");
        var url = platform switch
        {
            "Facebook" => $"https://www.facebook.com/sharer/sharer.php?u={Uri.EscapeDataString(c.BaseUrl)}&quote={q}",
            "X" => $"https://twitter.com/intent/tweet?text={q}",
            "LinkedIn" => $"https://www.linkedin.com/feed/?shareActive=true&text={q}",
            "WhatsApp Status" => $"https://wa.me/?text={q}",
            _ => null,
        };
        return Task.FromResult(url is null
            ? new ActionResultDto($"{platform} can't be opened from a link. Copy this text and post it in the {platform} app:\n\n{content}")
            : new ActionResultDto($"Opens {platform} with your text ready. Post it there, then mark this Posted here.", url));
    }

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var week = c.Today.AddDays(7);
        var scheduled = await c.Db.AppRecords.AsNoTracking().CountAsync(x => x.App == "social-marketing" && x.Status == "Scheduled" && x.Date <= week, ct);
        var overdue = await c.Db.AppRecords.AsNoTracking().CountAsync(x => x.App == "social-marketing" && x.Status == "Scheduled" && x.Date < c.Today, ct);
        var clicks = (await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "social-marketing" && x.Status == "Posted").Select(x => x.Data).ToListAsync(ct)).Sum(d => Dx.Parse(d).Int("clicks") ?? 0);
        return [new Card("Scheduled in the next 7 days", scheduled.ToString()), new Card("Past their date, not posted", overdue.ToString(), null, overdue > 0 ? "warn" : "ok"), new Card("Clicks on posted items", clicks.ToString())];
    }

    public IReadOnlyList<string> Flags(HookContext c, AppRecord r, JsonObject data) => r.Status == "Scheduled" && r.Date < c.Today ? ["Past its date and not posted"] : [];
}

/// <summary>Surveys: questions are one per line with their type after a bar; results count every answer.</summary>
public sealed class SurveysHook : IAppHook
{
    public string App => "surveys";

    public async Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (r.Kind != "survey") return;
        if (SurveyQuestions.Parse(data.Str("questions") ?? "").Count == 0) throw new BusinessRuleException("Write at least one question.");
        await AppHelpers.EnsureCodeAsync(c.Db, r, ct);
    }

    public async Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct)
    {
        var qs = SurveyQuestions.Parse(Dx.Parse(r.Data).Str("questions") ?? "");
        var answers = (await c.Db.AppRecords.AsNoTracking().Where(x => x.App == "surveys" && x.Kind == "response" && x.ParentId == r.Id).Select(x => x.Data).ToListAsync(ct))
            .Select(d => (Dx.Parse(d)["answers"] as JsonArray)?.Select(a => a?.ToString() ?? "").ToList() ?? []).ToList();
        var sb = new StringBuilder($"{answers.Count} response(s)\n");
        for (var i = 0; i < qs.Count; i++)
        {
            var q = qs[i];
            var col = answers.Select(a => i < a.Count ? a[i].Trim() : "").Where(a => a.Length > 0).ToList();
            sb.Append($"\n{i + 1}. {q.Text}\n");
            switch (q.Type)
            {
                case "rating":
                    var nums = col.Select(a => double.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : (double?)null).OfType<double>().ToList();
                    sb.Append(nums.Count == 0 ? "   No ratings yet\n" : $"   Average {nums.Average():0.0} out of 5 from {nums.Count} rating(s)\n");
                    break;
                case "yesno":
                    sb.Append($"   Yes: {col.Count(a => a.Equals("Yes", StringComparison.OrdinalIgnoreCase))}   No: {col.Count(a => a.Equals("No", StringComparison.OrdinalIgnoreCase))}\n");
                    break;
                case "choice":
                    foreach (var ch in q.Choices) sb.Append($"   {ch}: {col.Count(a => a == ch)}\n");
                    break;
                default:
                    foreach (var a in col.TakeLast(5)) sb.Append($"   “{a}”\n");
                    if (col.Count == 0) sb.Append("   No answers yet\n");
                    break;
            }
        }
        return new ActionResultDto(sb.ToString().TrimEnd());
    }
}
