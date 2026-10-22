using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Ledger;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Banking;

public sealed record BankAccountDto(int Id, string Name, string BankName, string AccountNumber, string GlCode, string Currency, bool Active, decimal LedgerBalance,
    int Unmatched, DateOnly? LastReconciled);
public sealed record BankAccountInput(string Name, string BankName, string AccountNumber, string? GlCode, string? Currency);
public sealed record StatementLineDto(long Id, DateOnly Date, string Description, string? Reference, decimal Amount, decimal? RunningBalance, string Status, long? GlLineId,
    string? MatchedTo);
public sealed record LedgerItemDto(long GlLineId, long JournalId, DateOnly Date, string Memo, string Source, decimal Amount, bool Cleared);
public sealed record ImportResult(int Imported, int Duplicates, int AutoMatched, IReadOnlyList<string> Problems);
public sealed record ReconcileViewDto(BankAccountDto Account, DateOnly AsOf, decimal StatementBalance, decimal LedgerBalance, decimal UnclearedReceipts,
    decimal UnclearedPayments, decimal AdjustedBankBalance, decimal Difference, IReadOnlyList<StatementLineDto> Statement, IReadOnlyList<LedgerItemDto> Uncleared,
    IReadOnlyList<ReconciliationDto> History);
public sealed record ReconciliationDto(int Id, DateOnly StatementDate, decimal StatementBalance, decimal LedgerBalance, decimal UnclearedReceipts, decimal UnclearedPayments,
    string Status, string? CompletedBy, DateTime? CompletedAt);
public sealed record PostFromStatementInput(string AccountCode, string? Memo);

/// <summary>
/// Bank reconciliation, the QuickBooks / Sage way. Import the bank's statement (CSV from internet banking), let the system match each line to the
/// ledger by amount and date, match the rest by hand or post what the books don't have yet (charges, interest, stamp duty), then agree the
/// statement balance: statement balance + receipts not yet on the statement − payments not yet on it = the ledger's bank balance.
/// </summary>
public sealed class BankReconciliationService(IBusinessDbContext db, TransactionRunner tx, IClock clock, GeneralLedger gl)
{
    private const int MatchWindowDays = 5;

    // ---------------------------------------------------------------- accounts
    private async Task EnsureDefaultAsync(CancellationToken ct)
    {
        if (await db.BankAccounts.AnyAsync(ct)) return;
        db.BankAccounts.Add(new BankAccount { Name = "Main bank account", BankName = "Bank", AccountNumber = "", GlCode = GlCodes.Bank });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<BankAccountDto>> AccountsAsync(CancellationToken ct = default)
    {
        await EnsureDefaultAsync(ct);
        var accounts = await db.BankAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var result = new List<BankAccountDto>();
        foreach (var a in accounts) result.Add(await DtoAsync(a, ct));
        return result;
    }

    private async Task<BankAccountDto> DtoAsync(BankAccount a, CancellationToken ct)
    {
        var balance = await LedgerBalanceAsync(a.GlCode, DateOnly.MaxValue, ct);
        var unmatched = await db.BankStatementLines.CountAsync(l => l.BankAccountId == a.Id && l.Status == StatementLineStatuses.Unmatched, ct);
        var last = await db.BankReconciliations.Where(r => r.BankAccountId == a.Id && r.Status == ReconciliationStatuses.Completed).MaxAsync(r => (DateOnly?)r.StatementDate, ct);
        return new BankAccountDto(a.Id, a.Name, a.BankName, a.AccountNumber, a.GlCode, a.Currency, a.Active, balance, unmatched, last);
    }

    /// <summary>Adds a bank account. Without a ledger code it gets its own (1011, 1012…), so each account reconciles separately.</summary>
    public async Task<int> AddAccountAsync(BankAccountInput i, CurrentUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Name the account.");
        await EnsureDefaultAsync(ct);
        await gl.AccountsAsync(ct);
        var code = string.IsNullOrWhiteSpace(i.GlCode) ? null : i.GlCode.Trim();
        if (code is null)
        {
            var used = await db.GlAccounts.Select(a => a.Code).ToListAsync(ct);
            code = Enumerable.Range(1011, 9).Select(n => n.ToString()).First(c => !used.Contains(c));
            db.GlAccounts.Add(new GlAccount { Code = code, Name = $"Bank — {i.Name.Trim()}", Type = GlAccountTypes.Asset });
        }
        else if (!await db.GlAccounts.AnyAsync(a => a.Code == code && a.Type == GlAccountTypes.Asset, ct)) throw new BusinessRuleException($"{code} isn't an asset account.");
        if (await db.BankAccounts.AnyAsync(a => a.GlCode == code, ct)) throw new BusinessRuleException($"Ledger account {code} already belongs to a bank account.");
        var a = new BankAccount { Name = i.Name.Trim(), BankName = i.BankName?.Trim() ?? "", AccountNumber = i.AccountNumber?.Trim() ?? "", GlCode = code,
            Currency = string.IsNullOrWhiteSpace(i.Currency) ? "NGN" : i.Currency.Trim().ToUpperInvariant() };
        db.BankAccounts.Add(a);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_ACCOUNT_ADDED", Entity = "BankAccount", At = clock.UtcNow, Detail = $"{a.Name} ({code})" });
        await db.SaveChangesAsync(ct);
        return a.Id;
    }

    public async Task UpdateAccountAsync(int id, BankAccountInput i, CurrentUser user, CancellationToken ct = default)
    {
        var a = await db.BankAccounts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Bank account");
        if (string.IsNullOrWhiteSpace(i.Name)) throw new BusinessRuleException("Name the account.");
        a.Name = i.Name.Trim(); a.BankName = i.BankName?.Trim() ?? ""; a.AccountNumber = i.AccountNumber?.Trim() ?? "";
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_ACCOUNT_UPDATED", Entity = "BankAccount", EntityId = id.ToString(), At = clock.UtcNow, Detail = a.Name });
        await db.SaveChangesAsync(ct);
    }

    private async Task<decimal> LedgerBalanceAsync(string code, DateOnly asOf, CancellationToken ct) =>
        await (from l in db.GlLines join j in db.GlJournals on l.JournalId equals j.Id join a in db.GlAccounts on l.AccountId equals a.Id
               where a.Code == code && j.Status != GlJournalStatuses.Draft && j.Date <= asOf
               select l.Debit - l.Credit).SumAsync(x => (decimal?)x, ct) ?? 0;

    // ---------------------------------------------------------------- import
    /// <summary>
    /// Imports a statement exported as CSV. Columns are found by their headings: a date (Trans Date / Value Date / Date), a description
    /// (Narration / Remarks / Details / Description), either Debit and Credit (Withdrawals / Lodgements, Money out / Money in) or a signed
    /// Amount, and optionally Reference and Balance. Lines already imported are skipped; the rest are matched to the ledger where certain.
    /// </summary>
    public async Task<ImportResult> ImportCsvAsync(int bankAccountId, string csv, CurrentUser user, CancellationToken ct = default)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account");
        var rows = ParseCsv(csv);
        if (rows.Count < 2) throw new BusinessRuleException("The file has no statement lines.");

        // The heading row is the first row naming a date column (banks often put account details above it).
        var headerAt = rows.FindIndex(r => r.Any(c => Is(c, "date", "trans date", "transaction date", "value date", "posting date", "tran date")));
        if (headerAt < 0) throw new BusinessRuleException("Couldn't find a Date column. Export the statement as CSV with its column headings.");
        var h = rows[headerAt].Select(c => c.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => h.FindIndex(c => names.Any(n => c == n || c.StartsWith(n + " ") || c.Replace(".", "").Replace("(₦)", "").Trim() == n));
        var date = Col("trans date", "transaction date", "date", "tran date", "posting date", "value date");
        var desc = Col("narration", "remarks", "description", "details", "transaction details", "particulars", "narrative");
        var debit = Col("debit", "withdrawals", "withdrawal", "money out", "debits", "dr");
        var credit = Col("credit", "lodgements", "lodgement", "deposits", "deposit", "money in", "credits", "cr");
        var amount = Col("amount", "transaction amount");
        var reference = Col("reference", "ref", "ref no", "reference number", "cheque no", "chq no");
        var balance = Col("balance", "running balance", "closing balance", "available balance");
        if (desc < 0) desc = h.FindIndex(c => c.Contains("narr") || c.Contains("remark") || c.Contains("descr") || c.Contains("detail"));
        if (debit < 0 && credit < 0 && amount < 0) throw new BusinessRuleException("Couldn't find Debit/Credit or Amount columns.");

        var problems = new List<string>();
        var batch = Guid.NewGuid().ToString("N")[..12];
        var existing = (await db.BankStatementLines.Where(l => l.BankAccountId == bankAccountId).Select(l => l.Fingerprint).ToListAsync(ct)).ToHashSet();
        var added = new List<BankStatementLine>();
        var dups = 0;
        for (var r = headerAt + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (!TryDate(Cell(date), out var d)) { if (Cell(date).Length > 0 && problems.Count < 10) problems.Add($"Row {r + 1}: unreadable date \"{Cell(date)}\" — skipped."); continue; }
            decimal value;
            if (amount >= 0 && debit < 0 && credit < 0) { if (!TryMoney(Cell(amount), out value)) { problems.Add($"Row {r + 1}: unreadable amount — skipped."); continue; } }
            else
            {
                TryMoney(Cell(credit), out var cr); TryMoney(Cell(debit), out var dr);
                value = Math.Abs(cr) - Math.Abs(dr);
            }
            if (value == 0) continue;
            decimal? bal = TryMoney(Cell(balance), out var b) && Cell(balance).Length > 0 ? b : null;
            var text = Cell(desc); if (text.Length > 300) text = text[..300];
            var refText = Cell(reference); if (refText.Length > 80) refText = refText[..80];
            var fp = Fingerprint(d, value, text, refText, bal);
            if (!existing.Add(fp)) { dups++; continue; }
            added.Add(new BankStatementLine { BankAccountId = bankAccountId, Date = d, Description = text, Reference = refText.Length > 0 ? refText : null, Amount = value,
                RunningBalance = bal, ImportBatch = batch, Fingerprint = fp });
        }
        if (added.Count == 0 && dups == 0) throw new BusinessRuleException(problems.FirstOrDefault() ?? "No statement lines could be read.");

        db.BankStatementLines.AddRange(added);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_STATEMENT_IMPORTED", Entity = "BankAccount", EntityId = bankAccountId.ToString(),
            At = clock.UtcNow, Detail = $"{account.Name}: {added.Count} line(s), {dups} already imported" });
        await db.SaveChangesAsync(ct);
        var matched = await AutoMatchAsync(bankAccountId, ct);
        return new ImportResult(added.Count, dups, matched, problems);
    }

    private static bool Is(string cell, params string[] names) => names.Contains(cell.Trim().ToLowerInvariant());

    private static string Fingerprint(DateOnly d, decimal amount, string text, string reference, decimal? balance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{d:yyyy-MM-dd}|{amount:0.00}|{text.ToLowerInvariant()}|{reference}|{balance:0.00}")))[..40];

    private static readonly string[] DateFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yy", "dd/MM/yy",
         "dd.MM.yyyy", "MMM dd, yyyy", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd-MMM-yyyy HH:mm:ss"];

    private static bool TryDate(string s, out DateOnly d)
    {
        d = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)) { d = DateOnly.FromDateTime(dt); return true; }
        if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out dt)) { d = DateOnly.FromDateTime(dt); return true; }
        return false;
    }

    private static bool TryMoney(string s, out decimal v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return true;
        var t = s.Replace("₦", "").Replace("NGN", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Replace(" ", "").Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')') || t.EndsWith("DR", StringComparison.OrdinalIgnoreCase);
        t = t.Trim('(', ')').Replace("DR", "", StringComparison.OrdinalIgnoreCase).Replace("CR", "", StringComparison.OrdinalIgnoreCase);
        if (t is "" or "-") return true;
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out v)) return false;
        if (negative) v = -Math.Abs(v);
        return true;
    }

    /// <summary>RFC 4180 CSV (quoted fields, embedded commas and quotes).</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',' ) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // ---------------------------------------------------------------- matching
    /// <summary>Ledger lines on the bank account not yet cleared by a statement line.</summary>
    private IQueryable<LedgerItemDto> UnclearedQuery(string code, DateOnly asOf)
    {
        var cleared = db.BankStatementLines.Where(s => s.GlLineId != null).Select(s => s.GlLineId!.Value);
        return from l in db.GlLines join j in db.GlJournals on l.JournalId equals j.Id join a in db.GlAccounts on l.AccountId equals a.Id
               where a.Code == code && j.Status != GlJournalStatuses.Draft && j.Date <= asOf && !cleared.Contains(l.Id)
               orderby j.Date, l.Id
               select new LedgerItemDto(l.Id, j.Id, j.Date, j.Memo, j.SourceType, l.Debit - l.Credit, false);
    }

    /// <summary>
    /// Matches each unmatched statement line to the one uncleared ledger line with the same amount within a few days — only when exactly one
    /// candidate fits, so a guess is never made. Returns how many were matched.
    /// </summary>
    public async Task<int> AutoMatchAsync(int bankAccountId, CancellationToken ct = default)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == bankAccountId, ct);
        var lines = await db.BankStatementLines.Where(l => l.BankAccountId == bankAccountId && l.Status == StatementLineStatuses.Unmatched).OrderBy(l => l.Date).ToListAsync(ct);
        if (lines.Count == 0) return 0;
        var uncleared = await UnclearedQuery(account.GlCode, DateOnly.MaxValue).ToListAsync(ct);
        var taken = new HashSet<long>();
        var matched = 0;
        foreach (var s in lines)
        {
            var candidates = uncleared.Where(u => !taken.Contains(u.GlLineId) && u.Amount == s.Amount && Math.Abs(u.Date.DayNumber - s.Date.DayNumber) <= MatchWindowDays).ToList();
            if (candidates.Count != 1) continue;
            s.GlLineId = candidates[0].GlLineId; s.Status = StatementLineStatuses.Matched; taken.Add(candidates[0].GlLineId); matched++;
        }
        await db.SaveChangesAsync(ct);
        return matched;
    }

    public async Task MatchAsync(long statementLineId, long glLineId, CurrentUser user, CancellationToken ct = default)
    {
        var s = await db.BankStatementLines.SingleOrDefaultAsync(l => l.Id == statementLineId, ct) ?? throw new NotFoundException("Statement line");
        if (s.Status != StatementLineStatuses.Unmatched) throw new BusinessRuleException("That statement line is already dealt with.");
        var account = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == s.BankAccountId, ct);
        var ledger = await UnclearedQuery(account.GlCode, DateOnly.MaxValue).SingleOrDefaultAsync(u => u.GlLineId == glLineId, ct)
                     ?? throw new BusinessRuleException("That ledger entry isn't on this bank account or is already cleared.");
        if (ledger.Amount != s.Amount) throw new BusinessRuleException($"The amounts differ (statement ₦{s.Amount:N2}, books ₦{ledger.Amount:N2}). Post the difference first.");
        s.GlLineId = glLineId; s.Status = StatementLineStatuses.Matched;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_LINE_MATCHED", Entity = "BankStatementLine", EntityId = s.Id.ToString(), At = clock.UtcNow,
            Detail = $"{s.Date:yyyy-MM-dd} {s.Amount:N2} → journal #{ledger.JournalId}" });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnmatchAsync(long statementLineId, CurrentUser user, CancellationToken ct = default)
    {
        var s = await db.BankStatementLines.SingleOrDefaultAsync(l => l.Id == statementLineId, ct) ?? throw new NotFoundException("Statement line");
        if (s.ReconciliationId is int rid && await db.BankReconciliations.AnyAsync(r => r.Id == rid && r.Status == ReconciliationStatuses.Completed, ct))
            throw new BusinessRuleException("This line is part of a completed reconciliation.");
        if (s.Status == StatementLineStatuses.Posted) throw new BusinessRuleException("An entry was posted from this line; reverse that journal instead.");
        s.GlLineId = null; s.Status = StatementLineStatuses.Unmatched;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_LINE_UNMATCHED", Entity = "BankStatementLine", EntityId = s.Id.ToString(), At = clock.UtcNow, Detail = s.Description });
        await db.SaveChangesAsync(ct);
    }

    public async Task IgnoreAsync(long statementLineId, CurrentUser user, CancellationToken ct = default)
    {
        var s = await db.BankStatementLines.SingleOrDefaultAsync(l => l.Id == statementLineId, ct) ?? throw new NotFoundException("Statement line");
        if (s.Status != StatementLineStatuses.Unmatched) throw new BusinessRuleException("Only an unmatched line can be ignored.");
        s.Status = StatementLineStatuses.Ignored;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_LINE_IGNORED", Entity = "BankStatementLine", EntityId = s.Id.ToString(), At = clock.UtcNow, Detail = s.Description });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Something on the statement the books don't have (bank charges, SMS alerts, stamp duty, interest): posts it against the chosen account and clears the line.</summary>
    public Task<long> PostFromStatementAsync(long statementLineId, PostFromStatementInput i, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var s = await db.BankStatementLines.SingleOrDefaultAsync(l => l.Id == statementLineId, inner) ?? throw new NotFoundException("Statement line");
            if (s.Status != StatementLineStatuses.Unmatched) throw new BusinessRuleException("That statement line is already dealt with.");
            var account = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == s.BankAccountId, inner);
            await gl.AccountsAsync(inner);
            var against = await db.GlAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Code == i.AccountCode, inner) ?? throw new BusinessRuleException("Choose the account it belongs to.");
            if (against.IsControl) throw new BusinessRuleException($"{against.Name} is posted by the system only; record the payment or receipt where it belongs, then match it.");
            if (against.Code == account.GlCode) throw new BusinessRuleException("Choose an account other than the bank itself.");
            var amt = Math.Abs(s.Amount);
            var j = await gl.PostAsync("BankStatement", s.Id.ToString(), "posted", s.Date, string.IsNullOrWhiteSpace(i.Memo) ? s.Description : i.Memo.Trim(),
                s.Amount > 0 ? [PostLine.Dr(account.GlCode, amt), PostLine.Cr(against.Code, amt)] : [PostLine.Dr(against.Code, amt), PostLine.Cr(account.GlCode, amt)], user, inner)
                ?? throw new BusinessRuleException("Nothing was posted.");
            await db.SaveChangesAsync(inner);
            var bankLine = await (from l in db.GlLines join a in db.GlAccounts on l.AccountId equals a.Id where l.JournalId == j.Id && a.Code == account.GlCode select l.Id).FirstAsync(inner);
            s.GlLineId = bankLine; s.Status = StatementLineStatuses.Posted;
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_LINE_POSTED", Entity = "BankStatementLine", EntityId = s.Id.ToString(), At = clock.UtcNow,
                Detail = $"{s.Description} ₦{s.Amount:N2} → {against.Code}" });
            await db.SaveChangesAsync(inner);
            return j.Id;
        }, ct);

    // ---------------------------------------------------------------- reconcile
    public async Task<ReconcileViewDto> ViewAsync(int bankAccountId, DateOnly? asOf, decimal? statementBalance, CancellationToken ct = default)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account");
        var date = asOf ?? clock.BusinessToday;
        var lines = await (from s in db.BankStatementLines.AsNoTracking()
                           where s.BankAccountId == bankAccountId && s.Date <= date
                           join l in db.GlLines on s.GlLineId equals l.Id into gj from l in gj.DefaultIfEmpty()
                           join j in db.GlJournals on l.JournalId equals j.Id into jj from j in jj.DefaultIfEmpty()
                           orderby s.Date descending, s.Id descending
                           select new StatementLineDto(s.Id, s.Date, s.Description, s.Reference, s.Amount, s.RunningBalance, s.Status, s.GlLineId,
                               j == null ? null : "#" + j.Id + " " + j.Memo)).Take(500).ToListAsync(ct);
        var uncleared = await UnclearedQuery(account.GlCode, date).ToListAsync(ct);
        var ledger = await LedgerBalanceAsync(account.GlCode, date, ct);
        // The statement's own closing balance when it gives one; else what the user typed.
        var stmtBalance = statementBalance ?? await db.BankStatementLines.AsNoTracking().Where(s => s.BankAccountId == bankAccountId && s.Date <= date && s.RunningBalance != null)
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.Id).Select(s => s.RunningBalance).FirstOrDefaultAsync(ct) ?? 0;
        var receipts = uncleared.Where(u => u.Amount > 0).Sum(u => u.Amount);
        var payments = -uncleared.Where(u => u.Amount < 0).Sum(u => u.Amount);
        var adjusted = stmtBalance + receipts - payments;
        var history = await db.BankReconciliations.AsNoTracking().Where(r => r.BankAccountId == bankAccountId).OrderByDescending(r => r.StatementDate).Take(24)
            .Select(r => new ReconciliationDto(r.Id, r.StatementDate, r.StatementBalance, r.LedgerBalance, r.UnclearedReceipts, r.UnclearedPayments, r.Status, r.CompletedByName, r.CompletedAt))
            .ToListAsync(ct);
        return new ReconcileViewDto(await DtoAsync(account, ct), date, stmtBalance, ledger, receipts, payments, adjusted, adjusted - ledger, lines, uncleared, history);
    }

    /// <summary>Signs off a reconciliation: only when the adjusted statement balance equals the ledger to the kobo.</summary>
    public Task<int> CompleteAsync(int bankAccountId, DateOnly statementDate, decimal statementBalance, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var v = await ViewAsync(bankAccountId, statementDate, statementBalance, inner);
            if (v.Difference != 0) throw new BusinessRuleException($"The reconciliation is out by ₦{v.Difference:N2}. Match or post the remaining statement lines first.");
            if (await db.BankReconciliations.AnyAsync(r => r.BankAccountId == bankAccountId && r.Status == ReconciliationStatuses.Completed && r.StatementDate >= statementDate, inner))
                throw new BusinessRuleException("This account is already reconciled to that date or later.");
            var r = new BankReconciliation { BankAccountId = bankAccountId, StatementDate = statementDate, StatementBalance = statementBalance, LedgerBalance = v.LedgerBalance,
                UnclearedReceipts = v.UnclearedReceipts, UnclearedPayments = v.UnclearedPayments, Status = ReconciliationStatuses.Completed,
                CompletedByUserId = user.Id, CompletedByName = user.FullName, CompletedAt = clock.UtcNow };
            db.BankReconciliations.Add(r);
            await db.SaveChangesAsync(inner);
            var lines = await db.BankStatementLines.Where(s => s.BankAccountId == bankAccountId && s.Date <= statementDate && s.ReconciliationId == null && s.Status != StatementLineStatuses.Unmatched).ToListAsync(inner);
            foreach (var s in lines) s.ReconciliationId = r.Id;
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "BANK_RECONCILED", Entity = "BankReconciliation", EntityId = r.Id.ToString(), At = clock.UtcNow,
                Detail = $"{v.Account.Name} to {statementDate:yyyy-MM-dd}: statement ₦{statementBalance:N2}, books ₦{v.LedgerBalance:N2}" });
            await db.SaveChangesAsync(inner);
            return r.Id;
        }, ct);
}
