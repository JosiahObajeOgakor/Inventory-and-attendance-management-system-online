using Inventory.Domain.Entities;

namespace Inventory.Application.Campaigns;

/// <summary>When a campaign is due. Pure functions of the campaign row and the clock, so the rules are easy to test and cannot depend on server time zone (Lagos is UTC+1 all year).</summary>
public static class CampaignSchedule
{
    public const int CatchUpMinutes = 180;
    private static readonly TimeSpan Lagos = TimeSpan.FromHours(1);

    public static (DateOnly Date, int Minute) LocalNow(DateTime utc)
    {
        var l = utc + Lagos;
        return (DateOnly.FromDateTime(l), l.Hour * 60 + l.Minute);
    }

    /// <summary>
    /// Due when: enabled, today is inside [StartDate, EndDate], it has not run today, the set time has passed (and not by more than 3 hours — a server that was down
    /// all morning skips the day instead of texting people at lunch), and today is one of the "every N days" days.
    /// </summary>
    public static bool IsDue(Campaign c, DateTime utcNow)
    {
        if (!c.Enabled) return false;
        var (today, minute) = LocalNow(utcNow);
        if (c.StartDate is { } s && today < s) return false;
        if (c.EndDate is { } e && today > e) return false;
        if (c.LastRunOn == today) return false;
        if (minute < c.RunMinute || minute > c.RunMinute + CatchUpMinutes) return false;
        var every = Math.Max(1, c.EveryNDays);
        if (every == 1) return true;
        if (c.StartDate is { } start) return (today.DayNumber - start.DayNumber) % every == 0;
        return c.LastRunOn is null || today.DayNumber - c.LastRunOn.Value.DayNumber >= every;
    }

    /// <summary>Customers are never messaged before 07:00 or after 20:30 Lagos time.</summary>
    public static bool IsQuietHour(int minute) => minute < 7 * 60 || minute > 20 * 60 + 30;

    public static string Describe(int runMinute) => $"{runMinute / 60:00}:{runMinute % 60:00}";
}
