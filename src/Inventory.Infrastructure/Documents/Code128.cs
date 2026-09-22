namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Code 128-B as a list of bar/space module widths. Ported from Barcodes.vb: a widths table plus a modulo-103 checksum,
/// readable by every hand scanner. Printable ASCII only.
/// </summary>
public static class Code128
{
    // Six digits per value (bar, space, bar, space, bar, space); the stop pattern carries a seventh module.
    private static readonly string[] Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    private const int StartB = 104;
    private const int Stop = 106;
    public const int QuietModules = 10;   // clear space each side, per the spec

    public static bool CanEncode(string? value) => !string.IsNullOrEmpty(value) && value.All(c => c is >= ' ' and <= '~');

    /// <returns>Alternating widths starting with a BAR, in modules, including no quiet zone.</returns>
    public static IReadOnlyList<int> Widths(string value)
    {
        if (!CanEncode(value)) throw new ArgumentException("Code 128-B only carries printable ASCII.", nameof(value));
        var codes = new List<int> { StartB };
        codes.AddRange(value.Select(c => c - 32));
        var sum = StartB;
        for (var i = 1; i < codes.Count; i++) sum += codes[i] * i;
        codes.Add(sum % 103);
        codes.Add(Stop);
        return codes.SelectMany(c => Patterns[c].Select(d => d - '0')).ToList();
    }

    public static int TotalModules(string value) => Widths(value).Sum() + QuietModules * 2;
}
