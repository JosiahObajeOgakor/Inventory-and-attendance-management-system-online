using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

/// <summary>Helpers the app hooks share.</summary>
public static class AppHelpers
{
    /// <summary>"Rice & Beans promo!" → "rice-beans-promo".</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "page" : s.Length > 60 ? s[..60].Trim('-') : s;
    }

    /// <summary>The record's own web address, made from the title if blank, with a number added if another record already has it.</summary>
    public static async Task EnsureCodeAsync(IBusinessDbContext db, AppRecord r, CancellationToken ct)
    {
        var want = string.IsNullOrWhiteSpace(r.Code) ? Slug(r.Title) : Slug(r.Code);
        var code = want; var n = 2;
        while (await db.AppRecords.AnyAsync(x => x.App == r.App && x.Kind == r.Kind && x.Code == code && x.Id != r.Id, ct)) code = $"{want}-{n++}";
        r.Code = code;
    }



    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);





    public static string Naira(decimal v) => "₦" + v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"Overdue" / "Due in 5 days" style note for a date, or null when it is comfortably far off.</summary>
    public static string? DueNote(string what, DateOnly? due, DateOnly today, int warnDays)
    {
        if (due is not DateOnly d) return null;
        var left = d.DayNumber - today.DayNumber;
        return left < 0 ? $"{what} expired {-left} day(s) ago" : left <= warnDays ? $"{what} expires in {left} day(s)" : null;
    }
}

/// <summary>Lines such as "Was it fast? | yesno": the questions of a survey.</summary>
public sealed record SurveyQuestion(string Text, string Type, string[] Choices);

public static class SurveyQuestions
{
    public static List<SurveyQuestion> Parse(string text)
    {
        var list = new List<SurveyQuestion>();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
            var q = parts[0]; if (q.Length == 0) continue;
            var kind = parts.Length > 1 ? parts[1] : "text";
            if (kind.StartsWith("choice", StringComparison.OrdinalIgnoreCase))
            {
                var choices = kind.Contains(':') ? kind[(kind.IndexOf(':') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
                list.Add(choices.Length >= 2 ? new SurveyQuestion(q, "choice", choices) : new SurveyQuestion(q, "text", []));
            }
            else list.Add(new SurveyQuestion(q, kind.ToLowerInvariant() is "rating" or "yesno" ? kind.ToLowerInvariant() : "text", []));
        }
        return list;
    }
}
