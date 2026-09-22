using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Maintenance;

public sealed class ArchiveOptions
{
    public const string Section = "Archive";
    /// <summary>Where archive ZIPs are kept on the server. Back this folder up with the rest of the VM.</summary>
    public string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "archives");
    /// <summary>0 = no fixed limit (the disk is the limit). Set it to the space you want to stay under, e.g. 40000 for 40 GB.</summary>
    public double DbSizeLimitMB { get; set; }
    public double WarnPercent { get; set; } = 80;
    /// <summary>Nothing newer than this can be archived, so a mistyped date can't sweep up recent trading.</summary>
    public int MinAgeDays { get; set; } = 90;
}

public sealed record DbUsage(double UsedMB, double LimitMB, double Percent, bool Warn, IReadOnlyList<TableSize> Tables);
public sealed record TableSize(string Table, long Rows, double MB);
public sealed record ArchivePreviewRow(string Table, int Rows);
public sealed record ArchiveResult(int Records, string File, long Bytes);
public sealed record ArchiveFile(string Name, long Bytes, DateTime CreatedUtc);

/// <summary>
/// Database size and "archive old records" (ported from DbMaintenance.vb). Archiving copies old, SETTLED records to a ZIP (one Excel workbook
/// plus a CSV per table) that stays on the server, then deletes them, all in ONE transaction: if the file can't be written nothing is deleted.
/// Customer and supplier balances, stock levels and loan balances are stored columns, not sums of history, so archiving never changes them.
/// Always kept, however old: unpaid or part-paid invoices, unpaid purchase orders, open staff loans, unredeemed rebates, and the stock
/// movements of any invoice that is kept (so it can still be voided). Improvement over the desktop app, which deleted every old movement.
/// </summary>
public sealed class ArchiveService(IBusinessDbContext db, TransactionRunner tx, IClock clock, ICompanyContext company, IOptions<ArchiveOptions> options)
{
    private ArchiveOptions Opt => options.Value;
    private const int XlsxRowLimit = 200_000;

    private string CompanyFolder => Path.Combine(Opt.Folder, new string(company.Key.Where(char.IsLetterOrDigit).ToArray()));

    // ---------------------------------------------------------------- size
    public async Task<DbUsage> UsageAsync(CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TABLE_NAME, COALESCE(TABLE_ROWS,0), (COALESCE(DATA_LENGTH,0)+COALESCE(INDEX_LENGTH,0)) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY 3 DESC";
            var tables = new List<TableSize>();
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) tables.Add(new TableSize(rd.GetString(0), Convert.ToInt64(rd.GetValue(1)), Math.Round(Convert.ToDouble(rd.GetValue(2)) / 1048576.0, 2)));
            var used = Math.Round(tables.Sum(t => t.MB), 1);
            var pct = Opt.DbSizeLimitMB > 0 ? Math.Round(used / Opt.DbSizeLimitMB * 100, 1) : 0;
            return new DbUsage(used, Opt.DbSizeLimitMB, pct, Opt.DbSizeLimitMB > 0 && pct >= Opt.WarnPercent, tables.Take(8).ToList());
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    // ---------------------------------------------------------------- what would be archived
    private sealed record Sets(List<int> Invoices, List<int> Orders, List<int> Loans);

    private async Task<Sets> SelectAsync(DateOnly cut, CancellationToken ct)
    {
        var invoices = await db.Invoices.AsNoTracking().Where(i => i.InvoiceDate < cut && (i.Status == PaymentStatuses.Paid || i.AmountPaid >= i.TotalAmount)).Select(i => i.Id).ToListAsync(ct);
        var orders = await db.PurchaseOrders.AsNoTracking().Where(p => p.OrderDate < cut && p.PaymentStatus == PaymentStatuses.Paid && (p.Status == PurchaseStatuses.Received || p.Status == PurchaseStatuses.Cancelled)).Select(p => p.Id).ToListAsync(ct);
        var loans = await db.EmployeeLoans.AsNoTracking().Where(l => l.Closed && l.LoanDate < cut && !db.LoanRepayments.Any(r => r.LoanId == l.Id && r.PaidDate >= cut)).Select(l => l.Id).ToListAsync(ct);
        return new Sets(invoices, orders, loans);
    }

    private IEnumerable<(string Name, IQueryable<object> Rows)> Specs(DateOnly cut, Sets s)
    {
        var cutUtc = cut.ToDateTime(TimeOnly.MinValue).AddHours(-1);
        yield return ("invoices", db.Invoices.AsNoTracking().Where(i => s.Invoices.Contains(i.Id)).Cast<object>());
        yield return ("invoice_items", db.InvoiceItems.AsNoTracking().Where(i => s.Invoices.Contains(i.InvoiceId)).Cast<object>());
        yield return ("payments", db.Payments.AsNoTracking().Where(p => s.Invoices.Contains(p.InvoiceId)).Cast<object>());
        yield return ("waybills", db.Waybills.AsNoTracking().Where(w => s.Invoices.Contains(w.InvoiceId)).Cast<object>());
        yield return ("rebate_entries", db.RebateEntries.AsNoTracking().Where(r => r.Status == "Redeemed" && r.EntryDate < cut).Cast<object>());
        yield return ("purchase_orders", db.PurchaseOrders.AsNoTracking().Where(p => s.Orders.Contains(p.Id)).Cast<object>());
        yield return ("purchase_order_items", db.PurchaseOrderItems.AsNoTracking().Where(i => s.Orders.Contains(i.PurchaseOrderId)).Cast<object>());
        yield return ("expenses", db.Expenses.AsNoTracking().Where(e => e.ExpenseDate < cut).Cast<object>());
        yield return ("ledger", db.Ledger.AsNoTracking().Where(l => l.EntryDate < cut).Cast<object>());
        yield return ("stock_movements", ArchivableMovements(cutUtc, s).AsNoTracking().Cast<object>());
        yield return ("employee_monthly", db.EmployeeMonthlies.AsNoTracking().Where(m => m.Paid && (m.PeriodYear < cut.Year || (m.PeriodYear == cut.Year && m.PeriodMonth < cut.Month))).Cast<object>());
        yield return ("employee_loans", db.EmployeeLoans.AsNoTracking().Where(l => s.Loans.Contains(l.Id)).Cast<object>());
        yield return ("loan_repayments", db.LoanRepayments.AsNoTracking().Where(r => s.Loans.Contains(r.LoanId)).Cast<object>());
    }

    /// <summary>Old stock movements, except those that belong to an invoice that is being kept (a kept invoice can still be voided, which needs its OUT movements).</summary>
    private IQueryable<StockMovement> ArchivableMovements(DateTime cutUtc, Sets s) =>
        db.StockMovements.Where(m => m.MovementDate < cutUtc && !(m.ReferenceType == MovementReferences.Invoice && m.ReferenceId != null && !s.Invoices.Contains(m.ReferenceId.Value)));

    private void CheckCutoff(DateOnly cut)
    {
        if (cut > clock.BusinessToday.AddDays(-Opt.MinAgeDays))
            throw new BusinessRuleException($"Choose a date at least {Opt.MinAgeDays} days ago. Recent records are never archived.");
    }

    public async Task<List<ArchivePreviewRow>> PreviewAsync(DateOnly cut, CancellationToken ct)
    {
        CheckCutoff(cut);
        var sets = await SelectAsync(cut, ct);
        var rows = new List<ArchivePreviewRow>();
        foreach (var (name, q) in Specs(cut, sets)) rows.Add(new ArchivePreviewRow(name, await q.CountAsync(ct)));
        return rows;
    }

    // ---------------------------------------------------------------- archive
    public Task<ArchiveResult> ArchiveAsync(DateOnly cut, CurrentUser user, CancellationToken ct)
    {
        CheckCutoff(cut);
        return tx.RunAsync(async inner =>
        {
            var sets = await SelectAsync(cut, inner);
            Directory.CreateDirectory(CompanyFolder);
            var stamp = clock.BusinessNow.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(CompanyFolder, $"Archive-before-{cut:yyyy-MM-dd}-{stamp}.zip");
            var total = 0;

            // 1. Write everything to the ZIP first. Any failure here throws before a single row is deleted.
            try
            {
                using var wb = new XLWorkbook();
                var about = wb.Worksheets.Add("About");
                await using (var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var (name, q) in Specs(cut, sets).Reverse())
                    {
                        var rows = await q.ToListAsync(inner);
                        total += rows.Count;
                        var table = ToTable(name, rows);
                        var entry = zip.CreateEntry($"csv/{name}.csv");
                        await using (var es = entry.Open()) await WriteCsv(es, table);
                        var ws = wb.Worksheets.Add(name);
                        if (rows.Count == 0) ws.Cell(1, 1).Value = "(nothing archived)";
                        else if (rows.Count > XlsxRowLimit) ws.Cell(1, 1).Value = $"{rows.Count:N0} rows: too many for one sheet, see csv/{name}.csv";
                        else { ws.Cell(1, 1).InsertTable(table, name, true); ws.Columns().AdjustToContents(1, 60); }
                    }
                    about.Cell(1, 1).Value = $"{company.LegalName} archive";
                    about.Cell(2, 1).Value = $"Settled records dated before {cut:yyyy-MM-dd}, removed from the database on {clock.BusinessNow:yyyy-MM-dd HH:mm} by {user.FullName}.";
                    about.Cell(3, 1).Value = $"{total:N0} records in total. Full copies are in the csv folder inside this ZIP.";
                    var xe = zip.CreateEntry("Archive.xlsx");
                    await using var xs = xe.Open(); wb.SaveAs(xs);
                }
            }
            catch { TryDelete(zipPath); throw; }

            // 2. Delete, children first. Kept rows that pointed at an archived invoice lose the link but keep their data.
            var inv = sets.Invoices;
            var cutUtc = cut.ToDateTime(TimeOnly.MinValue).AddHours(-1);
            await db.ProductSerials.Where(p => p.InvoiceId != null && inv.Contains(p.InvoiceId.Value)).ExecuteUpdateAsync(u => u.SetProperty(p => p.InvoiceId, (int?)null), inner);
            await db.Quotations.Where(qn => qn.ConvertedInvoiceId != null && inv.Contains(qn.ConvertedInvoiceId.Value)).ExecuteUpdateAsync(u => u.SetProperty(qn => qn.ConvertedInvoiceId, (int?)null), inner);
            await db.RebateEntries.Where(r => r.InvoiceId != null && inv.Contains(r.InvoiceId.Value) && r.Status != "Redeemed").ExecuteUpdateAsync(u => u.SetProperty(r => r.InvoiceId, (int?)null), inner);
            await db.InvoiceItems.Where(i => inv.Contains(i.InvoiceId)).ExecuteDeleteAsync(inner);
            await db.Payments.Where(p => inv.Contains(p.InvoiceId)).ExecuteDeleteAsync(inner);
            await db.Waybills.Where(w => inv.Contains(w.InvoiceId)).ExecuteDeleteAsync(inner);
            await db.RebateEntries.Where(r => r.Status == "Redeemed" && r.EntryDate < cut).ExecuteDeleteAsync(inner);
            await db.Invoices.Where(i => inv.Contains(i.Id)).ExecuteDeleteAsync(inner);
            await db.PurchaseOrderItems.Where(i => sets.Orders.Contains(i.PurchaseOrderId)).ExecuteDeleteAsync(inner);
            await db.PurchaseOrders.Where(p => sets.Orders.Contains(p.Id)).ExecuteDeleteAsync(inner);
            await db.Expenses.Where(e => e.ExpenseDate < cut).ExecuteDeleteAsync(inner);
            await db.Ledger.Where(l => l.EntryDate < cut).ExecuteDeleteAsync(inner);
            await ArchivableMovements(cutUtc, sets).ExecuteDeleteAsync(inner);
            await db.EmployeeMonthlies.Where(m => m.Paid && (m.PeriodYear < cut.Year || (m.PeriodYear == cut.Year && m.PeriodMonth < cut.Month))).ExecuteDeleteAsync(inner);
            await db.LoanRepayments.Where(r => sets.Loans.Contains(r.LoanId)).ExecuteDeleteAsync(inner);
            await db.EmployeeLoans.Where(l => sets.Loans.Contains(l.Id)).ExecuteDeleteAsync(inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "DATABASE_ARCHIVED", Entity = "Database", At = clock.UtcNow, Detail = $"before {cut:yyyy-MM-dd}: {total:N0} records → {Path.GetFileName(zipPath)}" });
            await db.SaveChangesAsync(inner);
            return new ArchiveResult(total, Path.GetFileName(zipPath), new FileInfo(zipPath).Length);
        }, ct);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort: the archive failed and nothing was deleted from the database */ } }

    // ---------------------------------------------------------------- files
    public IReadOnlyList<ArchiveFile> Files() =>
        !Directory.Exists(CompanyFolder) ? [] : new DirectoryInfo(CompanyFolder).GetFiles("Archive-*.zip").OrderByDescending(f => f.CreationTimeUtc).Select(f => new ArchiveFile(f.Name, f.Length, f.CreationTimeUtc)).ToList();

    /// <summary>The path of an archive by name, only if it is one of ours: no directory parts, right prefix and extension, inside this company's folder.</summary>
    public string? PathOf(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || !name.StartsWith("Archive-") || !name.EndsWith(".zip")) return null;
        var full = Path.GetFullPath(Path.Combine(CompanyFolder, name));
        return full.StartsWith(Path.GetFullPath(CompanyFolder) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    // ---------------------------------------------------------------- export helpers
    private static readonly Dictionary<Type, PropertyInfo[]> Props = [];

    private static DataTable ToTable(string name, List<object> rows)
    {
        var t = new DataTable(name);
        if (rows.Count == 0) return t;
        var type = rows[0].GetType();
        var props = type.GetProperties().Where(p => p.CanRead && IsScalar(p.PropertyType)).ToArray();
        foreach (var p in props) t.Columns.Add(p.Name, typeof(string));
        foreach (var r in rows) t.Rows.Add(props.Select(p => Format(p.GetValue(r))).Cast<object>().ToArray());
        return t;
    }

    private static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateOnly) || t.IsEnum;
    }

    private static string Format(object? v) => v switch
    {
        null => "", DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"), DateOnly d => d.ToString("yyyy-MM-dd"),
        decimal m => m.ToString(CultureInfo.InvariantCulture), bool b => b ? "1" : "0", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() ?? "",
    };

    private static async Task WriteCsv(Stream s, DataTable t)
    {
        await using var w = new StreamWriter(s, new UTF8Encoding(true), leaveOpen: true);
        static string Field(string v) => v.Length > 0 && (v[0] is '=' or '+' or '-' or '@') ? "\"'" + v.Replace("\"", "\"\"") + "\"" : "\"" + v.Replace("\"", "\"\"") + "\"";
        await w.WriteLineAsync(string.Join(",", t.Columns.Cast<DataColumn>().Select(c => Field(c.ColumnName))));
        foreach (DataRow r in t.Rows) await w.WriteLineAsync(string.Join(",", r.ItemArray.Select(x => Field(x?.ToString() ?? ""))));
    }
}
