namespace Inventory.Application.AppsEngine;

/// <summary>
/// Which record-keeping apps this installation has bought. There are no editions here, so the apps the client paid for are listed once and everything
/// else (menu, API, public pages) asks this. Set <c>Apps:Enabled</c> (comma-separated keys) to change the list without a release; an empty or missing
/// value means the bought list below.
/// </summary>
public sealed class AppAccess
{
    /// <summary>The apps in this upgrade: Money, People, Websites and Marketing (the sending apps follow the Campaigns rules: opted-in customers only).</summary>
    public static readonly IReadOnlyList<string> Bought =
    [
        "sign", "spreadsheet", "esg",
        "time-off", "recruitment", "appraisals", "fleet",
        "website", "events", "blog", "elearning", "forum",
        "social-marketing", "surveys", "email-marketing", "sms-marketing", "whatsapp", "marketing-automation",
    ];

    private readonly HashSet<string> _enabled;

    public AppAccess(string? configured, bool allowEmpty = false)
    {
        var asked = (configured ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Only apps that were actually built into this installation can be switched on.
        _enabled = (asked.Length == 0 && !allowEmpty ? Bought : asked).Where(Bought.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The Marketing apps: the ones a business with only Marketing switched on (<c>HasMarketing</c>) may use.</summary>
    public static readonly IReadOnlyList<string> Marketing = ["social-marketing", "surveys", "email-marketing", "sms-marketing", "whatsapp", "marketing-automation", "blog"];

    /// <summary>What one business may use: everything this installation has for a business that has the new features, the Marketing apps for one with
    /// only Marketing switched on, nothing for one that has neither yet.</summary>
    public AppAccess For(bool hasNewFeatures, bool hasMarketing = false) =>
        hasNewFeatures ? this : new AppAccess(hasMarketing ? string.Join(",", _enabled.Where(Marketing.Contains)) : "", allowEmpty: true);

    public IReadOnlyCollection<string> Enabled => _enabled;
    public bool IsEnabled(string key) => _enabled.Contains(key);
}
