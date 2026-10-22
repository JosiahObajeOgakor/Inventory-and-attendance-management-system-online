namespace Inventory.Application.SalesAssistant.Chat;

/// <summary>
/// Where a chat comes from, as far as we can tell without asking: the country from a WhatsApp number's calling code and, for Nigerian numbers,
/// the mobile network from the prefix (approximate: numbers can be ported between networks). Website visitors only give their browser's time zone.
/// </summary>
public static class ChatOrigin
{
    // Longest codes first so "1" (North America) never shadows "1x" codes in the future.
    private static readonly (string Code, string Country)[] Countries =
    [
        ("234", "Nigeria"), ("233", "Ghana"), ("229", "Benin"), ("228", "Togo"), ("225", "Côte d'Ivoire"), ("221", "Senegal"), ("237", "Cameroon"),
        ("254", "Kenya"), ("256", "Uganda"), ("255", "Tanzania"), ("250", "Rwanda"), ("27", "South Africa"), ("20", "Egypt"), ("212", "Morocco"),
        ("971", "UAE"), ("966", "Saudi Arabia"), ("44", "United Kingdom"), ("353", "Ireland"), ("49", "Germany"), ("33", "France"), ("39", "Italy"),
        ("34", "Spain"), ("31", "Netherlands"), ("86", "China"), ("91", "India"), ("61", "Australia"), ("1", "USA / Canada"),
    ];

    private static readonly Dictionary<string, string> NigerianPrefixes = new()
    {
        ["0703"] = "MTN", ["0704"] = "MTN", ["0706"] = "MTN", ["0803"] = "MTN", ["0806"] = "MTN", ["0810"] = "MTN", ["0813"] = "MTN", ["0814"] = "MTN",
        ["0816"] = "MTN", ["0903"] = "MTN", ["0906"] = "MTN", ["0913"] = "MTN", ["0916"] = "MTN",
        ["0701"] = "Airtel", ["0708"] = "Airtel", ["0802"] = "Airtel", ["0808"] = "Airtel", ["0812"] = "Airtel", ["0901"] = "Airtel", ["0902"] = "Airtel",
        ["0904"] = "Airtel", ["0907"] = "Airtel", ["0911"] = "Airtel", ["0912"] = "Airtel",
        ["0705"] = "Glo", ["0805"] = "Glo", ["0807"] = "Glo", ["0811"] = "Glo", ["0815"] = "Glo", ["0905"] = "Glo", ["0915"] = "Glo",
        ["0809"] = "9mobile", ["0817"] = "9mobile", ["0818"] = "9mobile", ["0908"] = "9mobile", ["0909"] = "9mobile",
    };

    /// <summary>Country and (Nigeria only) network for an international-format number such as "2348031234567" or "whatsapp:+2348031234567".</summary>
    public static (string? Country, string? Network) FromPhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return (null, null);
        var country = Countries.OrderByDescending(c => c.Code.Length).FirstOrDefault(c => digits.StartsWith(c.Code)).Country;
        if (country != "Nigeria" || digits.Length != 13) return (country, null);
        return (country, NigerianPrefixes.GetValueOrDefault("0" + digits.Substring(3, 3)) ?? "Other");
    }

    /// <summary>A browser time zone such as "Africa/Lagos", kept only if it looks like one.</summary>
    public static string? CleanTimeZone(string? tz)
    {
        tz = (tz ?? "").Trim();
        return tz.Length is > 2 and <= 60 && tz.All(ch => char.IsLetterOrDigit(ch) || ch is '/' or '_' or '-' or '+') ? tz : null;
    }
}
