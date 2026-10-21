using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using Microsoft.AspNetCore.Identity;
using Inventory.Domain;

namespace Inventory.Infrastructure.Persistence;

/// <summary>Companies come from configuration ("Companies" section), never from user input.</summary>
public sealed class CompanyRegistry
{
    private readonly IReadOnlyDictionary<string, CompanyInfo> _byKey;
    private readonly string _baseConnection;

    public CompanyRegistry(IConfiguration config)
    {
        _baseConnection = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured (set ConnectionStrings__Default in the environment).");
        IdentitySchema = config["IdentitySchema"] ?? "inventory_identity";
        var list = config.GetSection("Companies").Get<List<CompanyInfo>>() ?? [];
        if (list.Count == 0) throw new InvalidOperationException("No companies configured (Companies section).");
        _byKey = list.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        // Display order: "Order" from configuration (1, 2, 3…), otherwise the order they are listed in. Kept separate from the list position because
        // environment overrides address companies by position (Companies__0__Schema), so reordering the list itself would re-point those overrides.
        _ordered = list.Select((c, i) => (c, i)).OrderBy(x => x.c.Order > 0 ? x.c.Order : int.MaxValue).ThenBy(x => x.i).Select(x => x.c).ToList();
    }

    private readonly IReadOnlyList<CompanyInfo> _ordered;
    public string IdentitySchema { get; }
    public IEnumerable<CompanyInfo> All => _ordered;
    public CompanyInfo? Find(string? key) => key is not null && _byKey.TryGetValue(key, out var c) ? c : null;

    /// <summary>
    /// For CONFIGURATION only (never for logins or cookies): the company a setting means, forgiving a near-miss such as "chewypetsfeeds" for "chewypets".
    /// An exact key wins; otherwise the company whose key is the START of the given text ("chewypetsfeeds" starts with "chewypets"), but only if exactly one does.
    /// A fragment of a key ("che") is never enough: that would be a guess.
    /// </summary>
    public CompanyInfo? Resolve(string? text)
    {
        if (Find(text) is { } exact) return exact;
        static string N(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var wanted = N(text);
        if (wanted.Length < 3) return null;
        var matches = All.Where(c => { var k = N(c.Key); return k.Length >= 3 && wanted.StartsWith(k); }).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public string ConnectionFor(string schema) =>
        new MySqlConnectionStringBuilder(_baseConnection) { Database = schema }.ConnectionString;
    public string ServerConnection => new MySqlConnectionStringBuilder(_baseConnection) { Database = "" }.ConnectionString;

    public BusinessDbContext CreateFor(CompanyInfo company) =>
        new(new DbContextOptionsBuilder<BusinessDbContext>().UseMySQL(ConnectionFor(company.Schema)).Options);
}

/// <summary>Creates schemas (accent-sensitive, case-insensitive collation) and applies migrations at startup.</summary>
public sealed class DatabaseProvisioner(CompanyRegistry registry, IServiceProvider services)
{
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        await CreateSchemaAsync(registry.IdentitySchema, ct);
        foreach (var c in registry.All) await CreateSchemaAsync(c.Schema, ct);

        using (var scope = services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IdentityStore>();
            await store.Database.MigrateAsync(ct);
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
            foreach (var r in new[] { RoleNames.Admin, RoleNames.Clerk })
                if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole<int>(r));
        }
        foreach (var c in registry.All)
        {
            await using var db = registry.CreateFor(c);
            await db.Database.MigrateAsync(ct);
        }
    }

    private async Task CreateSchemaAsync(string schema, CancellationToken ct)
    {
        if (schema.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_'))) throw new InvalidOperationException("Invalid schema name.");
        await using var conn = new MySqlConnection(registry.ServerConnection);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{schema}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_ci", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Used only by `dotnet ef` at design time.</summary>
public sealed class BusinessDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<BusinessDbContext>
{
    public BusinessDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<BusinessDbContext>()
        .UseMySQL("Server=127.0.0.1;Database=design_time;User=root;Password=x").Options);
}

public sealed class IdentityStoreFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<IdentityStore>
{
    public IdentityStore CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<IdentityStore>()
        .UseMySQL("Server=127.0.0.1;Database=design_time;User=root;Password=x").Options);
}
