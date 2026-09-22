using System.Text.RegularExpressions;

namespace Inventory.Domain;

/// <summary>Ported from Barcodes.vb (EAN-13 helpers, in-store barcode minting, scanner payloads).</summary>
public static partial class Barcodes
{
    public static int Ean13CheckDigit(string twelveDigits)
    {
        if (twelveDigits is null || twelveDigits.Length != 12 || !twelveDigits.All(char.IsDigit))
            throw new ArgumentException("EAN-13 needs exactly 12 digits before the check digit.");
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (twelveDigits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    public static bool IsValidEan13(string? value) =>
        value is { Length: 13 } && value.All(char.IsDigit) && Ean13CheckDigit(value[..12]) == value[12] - '0';

    /// <summary>Prefix 200 is the GS1 range reserved for in-store use, so it cannot collide with a manufacturer code.</summary>
    public static string MintInternalBarcode(int productId)
    {
        var body = "200" + productId.ToString("D9");
        return body + Ean13CheckDigit(body);
    }

    public static string InvoiceTagPayload(string invoiceNumber, decimal total) =>
        $"CS|I|{invoiceNumber}|{total:0.00}";

    public static string SerialTagPayload(string sku, string serial) => $"CS|S|{sku}|{serial}";

    public sealed record Scan(string Kind, string Code, string? Sku);

    [GeneratedRegex(@"^CS\|(?<kind>[SI])\|(?<a>[^|]*)\|(?<b>[^|]*)$")]
    private static partial Regex TagPattern();

    /// <summary>Our own QR tags first, then anything else is a plain product barcode.</summary>
    public static Scan? ReadScan(string? input)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0) return null;
        var m = TagPattern().Match(raw);
        if (!m.Success) return new Scan("Barcode", raw, null);
        return m.Groups["kind"].Value == "S"
            ? new Scan("Serial", m.Groups["b"].Value, m.Groups["a"].Value)
            : new Scan("Invoice", m.Groups["a"].Value, null);
    }
}
