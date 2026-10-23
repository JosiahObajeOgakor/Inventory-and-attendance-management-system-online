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
            foreach (var r in RoleNames.All)
                if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole<int>(r));
            // A role gets its starting permissions once (while it has none); after that it belongs to the CEO. ADMIN also picks up permissions added
            // in later releases, so an upgrade never locks the owner out of a new feature. Roles kept from the old system follow their modern twin.
            var templates = RoleTemplates.All.Concat([new(RoleNames.LegacyAdmin, RoleTemplates.All[RoleNames.Admin]), new(RoleNames.LegacyClerk, RoleTemplates.All[RoleNames.Clerk])]);
            var seeded = new HashSet<int>();
            foreach (var (name, grants) in templates)
            {
                // role names match regardless of case ("Admin" is ADMIN): each role is seeded once
                if (await roles.FindByNameAsync(name) is not { } role || !seeded.Add(role.Id)) continue;
                var have = await store.RolePermissions.Where(p => p.RoleId == role.Id).Select(p => p.Permission).ToListAsync(ct);
                var add = have.Count == 0 ? grants : name is RoleNames.Admin or RoleNames.LegacyAdmin ? Permissions.All.Except(have).ToArray() : [];
                foreach (var p in add.Distinct()) store.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
            }
            await store.SaveChangesAsync(ct);
        }
        foreach (var c in registry.All)
        {
            await using var db = registry.CreateFor(c);
            await UndoHalfAppliedLiveLineMergeAsync(db, ct);
            await db.Database.MigrateAsync(ct);
        }
    }

    /// <summary>
    /// A deploy that was rolled back on 22 Oct 2026 left some schemas half-done before LiveLineMerge existed: four empty payment_links columns added
    /// and the status check dropped. Put payment_links back the way the live line had it so LiveLineMerge can add them again cleanly.
    /// Does nothing once LiveLineMerge is applied, or on a schema that has no payment_links table yet.
    /// </summary>
    private static async Task UndoHalfAppliedLiveLineMergeAsync(BusinessDbContext db, CancellationToken ct)
    {
        if (!(await db.Database.GetPendingMigrationsAsync(ct)).Any(m => m.EndsWith("_LiveLineMerge"))) return;
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        try
        {
            async Task<bool> Exists(string sql, string name)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.Add(new MySqlParameter("@name", name));
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
            }
            async Task Run(string sql) { await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(ct); }

            const string table = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @name";
            const string column = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payment_links' AND COLUMN_NAME = @name";
            const string check = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payment_links' AND CONSTRAINT_NAME = @name";
            if (!await Exists(table, "payment_links")) return;
            foreach (var name in new[] { "AccountNumber", "BankName", "ExpiresAt", "ExternalId" })
                if (await Exists(column, name)) await Run($"ALTER TABLE `payment_links` DROP COLUMN `{name}`");
            if (!await Exists(check, "CK_payment_links_status"))
                await Run("ALTER TABLE `payment_links` ADD CONSTRAINT `CK_payment_links_status` CHECK (Status IN ('Pending','Paid','PaidUnfulfilled','Cancelled'))");
        }
        finally { await conn.CloseAsync(); }
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
