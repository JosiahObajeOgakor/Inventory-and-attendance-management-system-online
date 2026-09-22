using System.IO.Compression;
using Inventory.Application.Common;
using Inventory.Infrastructure.Maintenance;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Inventory.IntegrationTests.MySqlFixture;

namespace Inventory.IntegrationTests;

[Collection("mysql")]
public class ArchiveTests(MySqlFixture mysql) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "inv-archive-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_folder, true); } catch { /* temp */ } }

    private ArchiveService Svc(BusinessDbContext db, string? folder = null) =>
        new(db, Wire.Tx(db), new SystemClock(), Wire.Co(), Options.Create(new ArchiveOptions { Folder = folder ?? _folder }));

    /// <summary>One old settled sale, one old unpaid sale, one recent sale. Everything old is dated 200 days back.</summary>
    private async Task<(string Cs, Seed Seed, int SettledId, int UnpaidId, int RecentId)> Scenario()
    {
        var cs = await mysql.NewSchemaAsync();
        var seed = await SeedAsync(cs, stock: 100);
        var old = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-200);
        async Task<int> Sell(DateOnly? date, decimal paid)
        {
            await using var s = NewContext(cs);
            var r = Sale(seed, 1, vat: 0, paid: paid); r.SaleDate = date;
            return (await SalesFor(s).SaveAsync(r, Clerk, null)).InvoiceId;
        }
        var settled = await Sell(old, 11500); var unpaid = await Sell(old, 0); var recent = await Sell(null, 11500);
        await using var db = NewContext(cs);
        await db.StockMovements.ExecuteUpdateAsync(u => u.SetProperty(m => m.MovementDate, DateTime.UtcNow.AddDays(-200)));   // make every movement old
        return (cs, seed, settled, unpaid, recent);
    }

    [Fact]
    public async Task Archiving_removes_settled_old_records_keeps_everything_else_and_leaves_balances_alone()
    {
        var (cs, seed, settled, unpaid, recent) = await Scenario();
        decimal balanceBefore;
        await using (var b = NewContext(cs)) balanceBefore = (await b.Customers.SingleAsync()).Balance;

        await using var db = NewContext(cs);
        var cut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100);
        var preview = await Svc(db).PreviewAsync(cut, default);
        Assert.Equal(1, preview.Single(p => p.Table == "invoices").Rows);
        var r = await Svc(db).ArchiveAsync(cut, Wire.Admin, default);
        Assert.True(r.Records > 0 && r.Bytes > 0);

        await using var check = NewContext(cs);
        var left = await check.Invoices.Select(i => i.Id).ToListAsync();
        Assert.DoesNotContain(settled, left);                     // old and settled: archived
        Assert.Contains(unpaid, left);                            // old but unpaid: always kept
        Assert.Contains(recent, left);                            // recent: kept
        Assert.Equal(2, await check.InvoiceItems.CountAsync());   // only the settled sale's line went
        Assert.Equal(1, await check.Payments.CountAsync());       // the recent payment stays
        Assert.Equal(balanceBefore, (await check.Customers.SingleAsync()).Balance);
        // The kept unpaid invoice can still be voided, so its OUT movement was kept even though it is old.
        Assert.Contains(await check.StockMovements.Select(m => m.ReferenceId).ToListAsync(), id => id == unpaid);
        Assert.DoesNotContain(await check.StockMovements.Select(m => m.ReferenceId).ToListAsync(), id => id == settled);
        Assert.Contains(await check.AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "DATABASE_ARCHIVED");
    }

    [Fact]
    public async Task The_zip_holds_an_excel_workbook_and_a_csv_per_table_with_the_archived_rows()
    {
        var (cs, _, settled, _, _) = await Scenario();
        await using var db = NewContext(cs);
        var r = await Svc(db).ArchiveAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100), Wire.Admin, default);

        var path = Svc(db).PathOf(r.File);
        Assert.NotNull(path);
        using var zip = ZipFile.OpenRead(path!);
        Assert.Contains(zip.Entries, e => e.FullName == "Archive.xlsx");
        var csv = zip.GetEntry("csv/invoices.csv")!;
        using var rd = new StreamReader(csv.Open());
        var text = await rd.ReadToEndAsync();
        Assert.Contains("InvoiceNumber", text);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);   // header + the one settled invoice
        Assert.Equal(settled, int.Parse(text.Split('\n')[1].Split(',')[0].Trim('"')));
    }

    [Fact]
    public async Task If_the_archive_file_cannot_be_written_nothing_is_deleted()
    {
        var (cs, _, _, _, _) = await Scenario();
        // A FILE where the company's folder should be makes the directory impossible to create.
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "test"), "in the way");

        await using var db = NewContext(cs);
        await Assert.ThrowsAnyAsync<Exception>(() => Svc(db).ArchiveAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100), Wire.Admin, default));
        await using var check = NewContext(cs);
        Assert.Equal(3, await check.Invoices.CountAsync());
        Assert.Equal(3, await check.InvoiceItems.CountAsync());
    }

    [Fact]
    public async Task Recent_dates_are_refused_and_download_names_cannot_escape_the_folder()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var svc = Svc(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ArchiveAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5), Wire.Admin, default));
        Assert.Null(svc.PathOf("../../etc/passwd"));
        Assert.Null(svc.PathOf("..\\secret.zip"));
        Assert.Null(svc.PathOf("Archive-x.zip"));   // well-formed but does not exist
    }

    [Fact]
    public async Task Usage_reports_the_size_of_this_companys_schema_and_warns_near_a_configured_limit()
    {
        var cs = await mysql.NewSchemaAsync();
        await using var db = NewContext(cs);
        var svc = new ArchiveService(db, Wire.Tx(db), new SystemClock(), Wire.Co(), Options.Create(new ArchiveOptions { DbSizeLimitMB = 0.5, WarnPercent = 50 }));
        var u = await svc.UsageAsync(default);
        Assert.True(u.UsedMB > 0);
        Assert.NotEmpty(u.Tables);
        Assert.True(u.Warn);   // an empty schema is still a few hundred KB, which is over 50% of a 0.5 MB limit
    }
}
