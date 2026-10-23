using System.Text.Json;
using Inventory.Application.Partners;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Partners;

public sealed class VerificationOptions
{
    public const string Section = "Verification";
    /// <summary>"dojah" to check CAC numbers with Dojah; empty for manual checks only.</summary>
    public string Provider { get; set; } = "";
    public string AppId { get; set; } = "";
    /// <summary>Environment only (Verification__SecretKey).</summary>
    public string SecretKey { get; set; } = "";
    /// <summary>https://api.dojah.io in production, https://sandbox.dojah.io while testing.</summary>
    public string BaseUrl { get; set; } = "https://api.dojah.io";
}

/// <summary>
/// Dojah's CAC lookup (GET /api/v1/kyc/cac/basic?rc_number=&amp;company_type=): the registry's name for the number is compared with the name the applicant gave.
/// A match is Verified, a different name is Mismatch (staff decide), an unknown number is NotFound.
/// </summary>
public sealed class DojahVerifier(HttpClient http, IOptions<VerificationOptions> options, ILogger<DojahVerifier> log) : IBusinessVerifier
{
    private VerificationOptions Opt => options.Value;
    public bool IsConfigured => Opt.Provider.Equals("dojah", StringComparison.OrdinalIgnoreCase) && Opt.AppId.Length > 0 && Opt.SecretKey.Length > 0;

    public async Task<VerificationResult> VerifyAsync(string cacNumber, string businessName, CancellationToken ct)
    {
        var digits = new string(cacNumber.Where(char.IsDigit).ToArray());
        // BN numbers are business names; RC (and bare numbers) are companies
        var type = cacNumber.StartsWith("BN", StringComparison.OrdinalIgnoreCase) ? "BUSINESS_NAME" : cacNumber.StartsWith("IT", StringComparison.OrdinalIgnoreCase) ? "INCORPORATED_TRUSTEES" : "COMPANY";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Opt.BaseUrl.TrimEnd('/')}/api/v1/kyc/cac/basic?rc_number={Uri.EscapeDataString(digits)}&company_type={type}");
        req.Headers.TryAddWithoutValidation("AppId", Opt.AppId);
        req.Headers.TryAddWithoutValidation("Authorization", Opt.SecretKey);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if ((int)res.StatusCode is 400 or 404) return new(PartnerVerifications.NotFound, null, "The registry has no business with this number.");
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Dojah CAC lookup answered {Status}", (int)res.StatusCode);
            return new(PartnerVerifications.Failed, null, $"The registry check answered {(int)res.StatusCode}. Try Re-check later.");
        }
        using var doc = JsonDocument.Parse(body);
        var entity = doc.RootElement.TryGetProperty("entity", out var e) ? e : doc.RootElement;
        var name = entity.TryGetProperty("company_name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return new(PartnerVerifications.NotFound, null, "The registry has no business with this number.");
        var status = entity.TryGetProperty("status", out var s) ? s.GetString() : null;
        var note = string.IsNullOrWhiteSpace(status) ? null : $"Registry status: {status}";
        return new(SameName(name, businessName) ? PartnerVerifications.Verified : PartnerVerifications.Mismatch, name.Trim(), note);
    }

    /// <summary>"PAWS &amp; CLAWS PET SUPPLIES LIMITED" and "Paws and Claws Pet Supplies Ltd" are the same business: compare the meaningful words.</summary>
    public static bool SameName(string registered, string given)
    {
        static HashSet<string> Words(string s) => s.ToUpperInvariant().Replace("&", " AND ")
            .Split([' ', '.', ',', '-', '(', ')', '\'', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("LIMITED" or "LTD" or "NIGERIA" or "NIG" or "ENTERPRISES" or "ENTERPRISE" or "VENTURES" or "AND" or "THE" or "COMPANY" or "CO" or "PLC" or "GLOBAL"))
            .ToHashSet();
        var a = Words(registered); var b = Words(given);
        if (a.Count == 0 || b.Count == 0) return false;
        return b.Count(a.Contains) >= Math.Max(1, (int)Math.Ceiling(Math.Min(a.Count, b.Count) * 0.75));
    }
}
