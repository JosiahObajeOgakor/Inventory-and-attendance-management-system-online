using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Inventory.MigrationTool;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

// Usage (see docs/MIGRATION_PLAN.md):
//   Inventory.MigrationTool --company chewypets --source "<SQL Server conn of the company DB>" --home-source "<SQL Server conn of the HOME DB>"
//        --target "<MySQL conn incl. Database=company_schema>" --identity "<MySQL conn incl. Database=identity_schema>"
//        [--mode plan|import] [--exclude-sample] [--utc-offset 1] [--report report.md] [--rebuild --confirm-drop <schema>]
//
// The SQL Server side is only ever read (use a db_datareader login). The MySQL side is written only if it is empty,
// or if --rebuild is given together with --confirm-drop <exact schema name>.

var opts = Args.Parse(args);
string Need(string k) => opts.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"Missing --{k}");

var company = Need("company");
var mode = opts.GetValueOrDefault("mode", "import");
var report = new Report();
report.Line($"# Migration report — {company}");
report.Line($"Run: {DateTime.UtcNow:u} · mode: {mode} · source time zone offset: UTC{(double.Parse(opts.GetValueOrDefault("utc-offset", "1")) >= 0 ? "+" : "")}{opts.GetValueOrDefault("utc-offset", "1")} · exclude sample: {opts.ContainsKey("exclude-sample")}");

try
{
    using var home = new SourceReader(Need("home-source"));
    using var src = new SourceReader(Need("source"));
    var options = new ImportOptions(TimeSpan.FromHours(double.Parse(opts.GetValueOrDefault("utc-offset", "1"))), opts.ContainsKey("exclude-sample"));

    if (mode == "plan")
    {
        report.H("Source inventory (nothing is written)");
        foreach (var t in new[] { "Users", "Categories", "Warehouses", "Products", "StockBatches", "StockMovements", "Suppliers", "PurchaseOrders", "Customers", "Invoices", "InvoiceItems", "Payments", "RebateEntries", "Ledger", "Expenses" })
            report.Line($"- {t}: {src.Scalar($"SELECT COUNT(*) AS v FROM [{t}]"):N0}");
        report.Line($"- flagged IsSample invoices: {src.Scalar("SELECT COUNT(*) AS v FROM Invoices WHERE IsSample = 1"):N0}");
        report.Line($"- products with blank (not NULL) barcode: {src.Scalar("SELECT COUNT(*) AS v FROM Products WHERE Barcode = ''"):N0}");
        report.Line($"- users still holding a placeholder hash: {home.Scalar("SELECT COUNT(*) AS v FROM Users WHERE PasswordHash NOT LIKE 'pbkdf2$%'"):N0}");
    }
    else
    {
        var targetCs = Need("target");
        var identityCs = Need("identity");
        var schema = new MySqlConnectionStringBuilder(targetCs).Database;
        var identitySchema = new MySqlConnectionStringBuilder(identityCs).Database;

        await EnsureSchemaAsync(targetCs, schema, opts, report);
        await EnsureSchemaAsync(identityCs, identitySchema, new Dictionary<string, string>(), report, allowExisting: true);

        await using var store = new IdentityStore(new DbContextOptionsBuilder<IdentityStore>().UseMySQL(identityCs).Options);
        await store.Database.MigrateAsync();
        var roleStore = new Microsoft.AspNetCore.Identity.EntityFrameworkCore.RoleStore<IdentityRole<int>, IdentityStore, int>(store);
        foreach (var r in new[] { RoleNames.Admin, RoleNames.Clerk })
            if (!await store.Roles.AnyAsync(x => x.Name == r)) { store.Roles.Add(new IdentityRole<int>(r) { NormalizedName = r.ToUpperInvariant(), ConcurrencyStamp = Guid.NewGuid().ToString() }); await store.SaveChangesAsync(); }

        report.H("Accounts (shared identity store)");
        var users = await IdentityImporter.ImportUsersAsync(home, store, report);
        await IdentityImporter.ImportLoginAuditAsync(home, store, report);

        await using var db = new BusinessDbContext(new DbContextOptionsBuilder<BusinessDbContext>().UseMySQL(targetCs).Options);
        await db.Database.MigrateAsync();
        if (await db.Products.AnyAsync() || await db.Invoices.AnyAsync() || await db.Customers.AnyAsync())
            throw new InvalidOperationException($"Target schema '{schema}' already contains data. Refusing to import into a non-empty schema (use --rebuild --confirm-drop {schema}).");

        var importer = new CompanyImporter(src, db, users, options, report);
        report.H("Import");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await importer.RunAsync();
            await tx.CommitAsync();   // all-or-nothing: a failure above leaves the schema empty
        }
        db.ChangeTracker.Clear();
        report.Line("Import committed.");
        await importer.VerifyAsync();
        importer.ReportSkippedTables();
    }
}
catch (Exception ex)
{
    report.H("ABORTED");
    report.Fail(ex.Message);
}

report.H("Result");
report.Line(report.Failures.Count == 0
    ? $"**PASS** — {report.Warnings.Count} warning(s) to review."
    : $"**FAIL** — {report.Failures.Count} failure(s), {report.Warnings.Count} warning(s).");
var text = report.ToString();
Console.WriteLine(text);
if (opts.TryGetValue("report", out var path)) File.WriteAllText(path, text);
return report.Failures.Count == 0 ? 0 : 1;

static async Task EnsureSchemaAsync(string cs, string schema, Dictionary<string, string> opts, Report report, bool allowExisting = false)
{
    if (schema.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) throw new ArgumentException("Invalid schema name.");
    var server = new MySqlConnectionStringBuilder(cs) { Database = "" }.ConnectionString;
    await using var conn = new MySqlConnection(server);
    await conn.OpenAsync();
    if (opts.ContainsKey("rebuild"))
    {
        if (opts.GetValueOrDefault("confirm-drop") != schema) throw new InvalidOperationException($"--rebuild needs --confirm-drop {schema} (exact schema name).");
        await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{schema}`", conn);
        await drop.ExecuteNonQueryAsync();
        report.Line($"- dropped and recreating schema {schema}");
    }
    await using var cmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{schema}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_ci", conn);
    await cmd.ExecuteNonQueryAsync();
}

static class Args
{
    public static Dictionary<string, string> Parse(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var key = a[i][2..];
            d[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
        }
        return d;
    }
}
