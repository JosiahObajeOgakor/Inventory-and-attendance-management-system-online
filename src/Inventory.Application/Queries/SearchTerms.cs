using System.Globalization;

namespace Inventory.Application.Queries;

/// <summary>
/// How the search boxes read what was typed. Several words narrow the list (each word must match something on the row, in any order), phone numbers are
/// found however they were typed or stored (0803 111 0001, +234 803 111 0001, 8031110001), and amounts and dates typed as such match those columns.
/// </summary>
public static class SearchTerms
{
    /// <summary>The words typed (at most six), trimmed and without duplicates.</summary>
    public static string[] Words(string? term) =>
        (term ?? "").Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();

    /// <summary>The digits of a phone-looking word without the leading 0 or 234, to match against a stored number with its spaces and signs removed. Empty when the word isn't a phone number (fewer than 4 digits, or letters in it).</summary>
    public static string PhoneNeedle(string word)
    {
        var w = word.Replace("+", "").Replace("-", "").Replace("(", "").Replace(")", "");
        if (w.Length < 4 || !w.All(char.IsAsciiDigit)) return "";
        if (w.StartsWith("234") && w.Length >= 10) w = w[3..];
        else if (w.StartsWith('0') && w.Length > 4) w = w[1..];
        return w;
    }

    /// <summary>A money amount typed as 5000, 5,000 or 5000.50 (at least two digits, so "7" doesn't match every ₦7).</summary>
    public static decimal? Amount(string word)
    {
        var w = word.Replace(",", "").TrimStart('₦', 'N', 'n');
        return w.Length >= 2 && w.All(c => char.IsAsciiDigit(c) || c == '.') && decimal.TryParse(w, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>A date typed as 2026-10-21, 2026-10 (the whole month), 21/10/2026 or 21-10-2026, as the first and last day it covers.</summary>
    public static (DateOnly From, DateOnly To)? Dates(string word)
    {
        if (DateOnly.TryParseExact(word, ["yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return (day, day);
        if (DateTime.TryParseExact(word, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            var first = new DateOnly(month.Year, month.Month, 1);
            return (first, first.AddMonths(1).AddDays(-1));
        }
        return null;
    }

    /// <summary>The same range as whole days of a UTC timestamp, for columns that store a date and time.</summary>
    public static (DateTime From, DateTime To)? Moments(string word) =>
        Dates(word) is { } d ? (d.From.ToDateTime(TimeOnly.MinValue).AddHours(-1), d.To.AddDays(1).ToDateTime(TimeOnly.MinValue).AddHours(-1)) : null;
}
