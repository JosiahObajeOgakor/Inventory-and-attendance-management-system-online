using System.Security.Cryptography;
using Inventory.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Identity;

/// <summary>Accounts are shared by both businesses (as in the desktop app) and live in one identity schema.</summary>
public class AppUser : IdentityUser<int>
{
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Comma-separated company keys this user may open, e.g. "chewypets,candid".</summary>
    public string CompanyAccess { get; set; } = "";

    public bool CanOpen(string companyKey) =>
        CompanyAccess.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(companyKey, StringComparer.OrdinalIgnoreCase);
}

public class LoginAuditEntry
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public bool Succeeded { get; set; }
    public string? Reason { get; set; }
    /// <summary>Client IP (the desktop app stored the machine name).</summary>
    public string? ClientAddress { get; set; }
    public DateTime AtUtc { get; set; }
}

public class IdentityStore(DbContextOptions<IdentityStore> options)
    : IdentityDbContext<AppUser, IdentityRole<int>, int>(options)
{
    public DbSet<LoginAuditEntry> LoginAudit => Set<LoginAuditEntry>();
    public DbSet<Inventory.Infrastructure.Ai.AiUsageEntry> AiUsage => Set<Inventory.Infrastructure.Ai.AiUsageEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(100).IsRequired();
            e.Property(x => x.CompanyAccess).HasMaxLength(200).IsRequired();
            e.Property(x => x.LastLoginAt).HasPrecision(6);
            e.Property(x => x.CreatedAt).HasPrecision(6);
            // Identity's defaults (PasswordHash MAX etc.) are fine; the legacy pbkdf2 string is < 256 chars.
        });
        b.Entity<Inventory.Infrastructure.Ai.AiUsageEntry>(e =>
        {
            e.ToTable("ai_usage");
            e.Property(x => x.AtUtc).HasPrecision(6);
            e.HasIndex(x => new { x.UserId, x.AtUtc });
        });
        b.Entity<LoginAuditEntry>(e =>
        {
            e.ToTable("login_audit");
            e.Property(x => x.Username).HasMaxLength(50).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(100);
            e.Property(x => x.ClientAddress).HasMaxLength(64);
            e.Property(x => x.AtUtc).HasPrecision(6);
            e.HasIndex(x => x.AtUtc);
        });
    }
}

/// <summary>
/// Verifies the desktop app's hashes ("pbkdf2$120000$salt$hash", PBKDF2-HMAC-SHA256, rule A1) so existing users keep their
/// passwords, and asks Identity to re-hash them to its own format on the first successful login. Placeholder hashes
/// ("SETUP_REQUIRED", "CHANGE_ME_…") NEVER verify — unlike the desktop app, which let anyone into such an account to set a
/// password; on the web an admin must issue the first password.
/// </summary>
public sealed class LegacyAwarePasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _identity = new();

    public string HashPassword(AppUser user, string password) => _identity.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword)) return PasswordVerificationResult.Failed;
        if (!hashedPassword.StartsWith("pbkdf2$", StringComparison.Ordinal))
            return hashedPassword.StartsWith("SETUP_REQUIRED") || hashedPassword.StartsWith("CHANGE_ME")
                ? PasswordVerificationResult.Failed
                : _identity.VerifyHashedPassword(user, hashedPassword, providedPassword);

        return VerifyLegacy(hashedPassword, providedPassword) ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Failed;
    }

    public static bool VerifyLegacy(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Test helper: produce a desktop-format hash exactly as Security.HashPassword did.</summary>
    public static string MakeLegacyHash(string password, int iterations = 120_000)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}

/// <summary>Rule A: ≥ 8 chars, letters and digits, no leading/trailing spaces, small blocklist (ported from Security.PasswordProblem).</summary>
public sealed class DesktopPasswordPolicy : IPasswordValidator<AppUser>
{
    private static readonly string[] Weak = ["password", "12345678", "qwerty", "admin123", "chewypets"];

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        string? problem = null;
        if (password is null || password.Length < 8) problem = "Use at least 8 characters.";
        else if (!(password.Any(char.IsLetter) && password.Any(char.IsDigit))) problem = "Mix letters and numbers.";
        else if (password.Trim().Length != password.Length) problem = "Remove leading/trailing spaces.";
        else if (Weak.Any(w => password.Contains(w, StringComparison.OrdinalIgnoreCase))) problem = "That password is too easy to guess.";
        return Task.FromResult(problem is null ? IdentityResult.Success : IdentityResult.Failed(new IdentityError { Code = "WeakPassword", Description = problem }));
    }
}
