using System.Globalization;
using System.Text;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Ledger;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Tax;

public sealed record VatLine(DateOnly Date, string Reference, string Party, decimal Net, decimal Vat);
public sealed record VatReturnDto(int Year, int Month, decimal OutputVat, decimal InputVat, decimal NetPayable, decimal CreditBroughtForward, decimal Payable,
    IReadOnlyList<VatLine> Sales, DateOnly DueDate, bool Filed, decimal AmountPaid, string? Reference);
public sealed record FileVatInput(int Year, int Month, DateOnly? PaidOn, string? Reference);

/// <summary>The monthly VAT return (VAT charged on sales, due by the 21st of the next month) and the ledger posting that settles it.</summary>
public sealed class TaxService(IBusinessDbContext db, TransactionRunner tx, IClock clock, GeneralLedger gl, LedgerSync sync)
{
    private static string Period(int y, int m) => $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m)} {y}";
    private static DateOnly Due(int y, int m) => new DateOnly(y, m, 1).AddMonths(1).AddDays(20);

    /// <summary>Net movement on one account for journals in a month, excluding the return's own settlement.</summary>
    private async Task<decimal> MonthNetAsync(string code, int year, int month, bool creditNormal, CancellationToken ct)
    {
        var first = new DateOnly(year, month, 1); var last = first.AddMonths(1).AddDays(-1);
        var q = from l in db.GlLines join j in db.GlJournals on l.JournalId equals j.Id join a in db.GlAccounts on l.AccountId equals a.Id
                where a.Code == code && j.Status != GlJournalStatuses.Draft && j.Date >= first && j.Date <= last && j.SourceType != "TaxFiling"
                select creditNormal ? l.Credit - l.Debit : l.Debit - l.Credit;
        return await q.SumAsync(x => (decimal?)x, ct) ?? 0;
    }

    public async Task<VatReturnDto> VatReturnAsync(int year, int month, CancellationToken ct = default)
    {
        if (month is < 1 or > 12) throw new BusinessRuleException("Choose a valid month.");
        await sync.RunAsync(ct);
        var first = new DateOnly(year, month, 1); var last = first.AddMonths(1).AddDays(-1);
        var output = await MonthNetAsync(GlCodes.VatOutput, year, month, creditNormal: true, ct);
        var input = await MonthNetAsync(GlCodes.VatInput, year, month, creditNormal: false, ct);
        var sales = await (from i in db.Invoices.AsNoTracking() join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id
                           where i.InvoiceDate >= first && i.InvoiceDate <= last && i.VatAmount != 0 && i.VoidedAt == null && !i.IsSample
                           orderby i.InvoiceDate select new VatLine(i.InvoiceDate, i.InvoiceNumber, c.Name, i.TotalAmount - i.VatAmount, i.VatAmount)).ToListAsync(ct);

        // Input tax not used up in earlier months is carried forward.
        var broughtForward = Math.Max(0, await (from l in db.GlLines join j in db.GlJournals on l.JournalId equals j.Id join a in db.GlAccounts on l.AccountId equals a.Id
                                                where a.Code == GlCodes.VatInput && j.Status != GlJournalStatuses.Draft && j.Date < first
                                                select l.Debit - l.Credit).SumAsync(x => (decimal?)x, ct) ?? 0);
        var net = output - input;
        var payable = Math.Max(0, net - broughtForward);
        var filed = await db.TaxFilings.AsNoTracking().FirstOrDefaultAsync(f => f.Kind == TaxKinds.Vat && f.Year == year && f.Month == month, ct);
        return new VatReturnDto(year, month, output, input, net, broughtForward, payable, sales, Due(year, month), filed is not null, filed?.AmountPaid ?? 0, filed?.Reference);
    }

    /// <summary>Files and pays a month's VAT: output tax is cleared against any input tax and the rest paid from the bank.</summary>
    public async Task<int> FileVatAsync(FileVatInput i, CurrentUser user, CancellationToken ct = default)
    {
        await sync.RunAsync(ct);
        return await tx.RunAsync(async inner =>
        {
            if (await db.TaxFilings.AnyAsync(f => f.Kind == TaxKinds.Vat && f.Year == i.Year && f.Month == i.Month, inner))
                throw new BusinessRuleException($"VAT for {Period(i.Year, i.Month)} has already been filed.");
            var r = await VatReturnAsync(i.Year, i.Month, inner);
            if (r.OutputVat <= 0 && r.InputVat <= 0) throw new BusinessRuleException("There is no VAT to file for this month.");
            var setOff = Math.Min(Math.Max(0, r.OutputVat), r.InputVat + r.CreditBroughtForward);
            var f = new TaxFiling { Kind = TaxKinds.Vat, Year = i.Year, Month = i.Month, Authority = "NRS", TaxDue = r.OutputVat, Credits = setOff, AmountPaid = r.Payable,
                PaidOn = i.PaidOn ?? clock.BusinessToday, Reference = string.IsNullOrWhiteSpace(i.Reference) ? null : i.Reference.Trim(), CreatedByUserId = user.Id, CreatedAt = clock.UtcNow };
            db.TaxFilings.Add(f);
            await db.SaveChangesAsync(inner);
            await gl.PostAsync("TaxFiling", f.Id.ToString(), "vat", f.PaidOn, $"VAT return {Period(i.Year, i.Month)}",
                [PostLine.Dr(GlCodes.VatOutput, setOff + r.Payable), PostLine.Cr(GlCodes.VatInput, setOff), PostLine.Cr(GlCodes.Bank, r.Payable)], user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "VAT_FILED", Entity = "TaxFiling", EntityId = f.Id.ToString(), At = clock.UtcNow,
                Detail = $"{Period(i.Year, i.Month)}: output {r.OutputVat:N2}, input {r.InputVat:N2}, paid {r.Payable:N2} ref {f.Reference}" });
            await db.SaveChangesAsync(inner);
            return f.Id;
        }, ct);
    }

    public async Task<string> VatCsvAsync(int year, int month, CancellationToken ct = default)
    {
        var r = await VatReturnAsync(year, month, ct);
        var sb = new StringBuilder("Section,Date,Reference,Party,Net,VAT\r\n");
        foreach (var l in r.Sales) sb.Append(Csv("Sales (output)", l.Date.ToString("yyyy-MM-dd"), l.Reference, l.Party, F(l.Net), F(l.Vat)));
        sb.Append(Csv("Total output VAT", "", "", "", "", F(r.OutputVat))).Append(Csv("Total input VAT", "", "", "", "", F(r.InputVat)))
          .Append(Csv("Credit brought forward", "", "", "", "", F(r.CreditBroughtForward))).Append(Csv("VAT payable", "", "", "", "", F(r.Payable)));
        return sb.ToString();
    }

    private static string F(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Csv(params string?[] cells) =>
        string.Join(",", cells.Select(c =>
        {
            var v = c ?? "";
            if (v.Length > 0 && "=+-@".Contains(v[0]) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        })) + "\r\n";
}
