using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Ledger;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Currencies;

public sealed record CurrencyDto(string Code, string Name, string Symbol, bool Active, decimal? LatestRate, DateOnly? LatestDate);
public sealed record RateDto(int Id, string Currency, DateOnly Date, decimal Rate, string? Source);
public sealed record NewFxDocumentInput(string Kind, int PartyId, string Currency, DateOnly Date, DateOnly? DueDate, string Description, string AccountCode,
    decimal ForeignAmount, decimal? Rate);
public sealed record SettleInput(DateOnly Date, decimal ForeignAmount, decimal? Rate, string? BankCode);
public sealed record FxDocumentDto(int Id, string Number, string Kind, string Party, string Currency, DateOnly Date, DateOnly? DueDate, string Description,
    decimal ForeignAmount, decimal Rate, decimal BaseAmount, decimal ForeignOutstanding, bool Settled, IReadOnlyList<FxSettlementDto> Settlements);
public sealed record FxSettlementDto(DateOnly Date, decimal ForeignAmount, decimal Rate, decimal BaseAmount, decimal GainLoss);
public sealed record RevaluationRow(string Number, string Kind, string Party, string Currency, decimal ForeignOutstanding, decimal BookRate, decimal ClosingRate, decimal Unrealised);
public sealed record RevaluationDto(DateOnly AsOf, IReadOnlyList<RevaluationRow> Rows, decimal TotalUnrealised, bool Posted);

/// <summary>
/// Foreign currencies (IAS 21 in outline). Every amount in the books is naira. A foreign bill or invoice is translated at the rate on its date;
/// settling it at another rate posts the difference as a realised exchange gain or loss; at a month end, open foreign balances can be revalued at
/// the closing rate (unrealised gain or loss, reversed the next day so the settlement later realises the full difference).
/// </summary>
public sealed class CurrencyService(IBusinessDbContext db, TransactionRunner tx, IClock clock, GeneralLedger gl)
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static readonly (string Code, string Name, string Symbol)[] Common =
        [("USD", "US dollar", "$"), ("GBP", "Pound sterling", "£"), ("EUR", "Euro", "€"), ("CNY", "Chinese yuan", "¥"), ("GHS", "Ghanaian cedi", "GH₵"), ("XOF", "CFA franc (BCEAO)", "CFA"), ("ZAR", "South African rand", "R")];

    public async Task<List<CurrencyDto>> CurrenciesAsync(CancellationToken ct = default)
    {
        var currencies = await db.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);
        var result = new List<CurrencyDto>();
        foreach (var c in currencies)
        {
            var latest = await db.ExchangeRates.AsNoTracking().Where(r => r.Currency == c.Code).OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            result.Add(new CurrencyDto(c.Code, c.Name, c.Symbol, c.Active, latest?.Rate, latest?.Date));
        }
        return result;
    }

    public async Task AddCurrencyAsync(string code, string? name, string? symbol, CurrentUser user, CancellationToken ct = default)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper)) throw new BusinessRuleException("Use the three-letter ISO code, e.g. USD.");
        if (code == "NGN") throw new BusinessRuleException("Naira is the base currency.");
        if (await db.Currencies.AnyAsync(c => c.Code == code, ct)) throw new BusinessRuleException($"{code} is already set up.");
        var known = Common.FirstOrDefault(c => c.Code == code);
        db.Currencies.Add(new Currency { Code = code, Name = string.IsNullOrWhiteSpace(name) ? known.Name ?? code : name.Trim(), Symbol = string.IsNullOrWhiteSpace(symbol) ? known.Symbol ?? code : symbol.Trim() });
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "CURRENCY_ADDED", Entity = "Currency", EntityId = code, At = clock.UtcNow, Detail = code });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<RateDto>> RatesAsync(string currency, CancellationToken ct = default) =>
        await db.ExchangeRates.AsNoTracking().Where(r => r.Currency == currency).OrderByDescending(r => r.Date).Take(120)
            .Select(r => new RateDto(r.Id, r.Currency, r.Date, r.Rate, r.Source)).ToListAsync(ct);

    public async Task SetRateAsync(string currency, DateOnly date, decimal rate, string? source, CurrentUser user, CancellationToken ct = default)
    {
        if (rate <= 0) throw new BusinessRuleException("Enter how many naira one unit costs.");
        if (!await db.Currencies.AnyAsync(c => c.Code == currency, ct)) throw new BusinessRuleException($"Set up {currency} first.");
        var r = await db.ExchangeRates.SingleOrDefaultAsync(x => x.Currency == currency && x.Date == date, ct);
        if (r is null) { r = new ExchangeRate { Currency = currency, Date = date }; db.ExchangeRates.Add(r); }
        r.Rate = rate; r.Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "FX_RATE_SET", Entity = "ExchangeRate", EntityId = currency, At = clock.UtcNow, Detail = $"{currency} {date:yyyy-MM-dd} ₦{rate:N4}" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The rate in force on a date (the latest on or before it).</summary>
    public async Task<decimal> RateOnAsync(string currency, DateOnly date, CancellationToken ct) =>
        await db.ExchangeRates.AsNoTracking().Where(r => r.Currency == currency && r.Date <= date).OrderByDescending(r => r.Date).Select(r => (decimal?)r.Rate).FirstOrDefaultAsync(ct)
        ?? throw new BusinessRuleException($"There is no {currency} rate on or before {date:dd MMM yyyy}. Enter one first.");

    // ---------------------------------------------------------------- documents
    public async Task<List<FxDocumentDto>> DocumentsAsync(bool openOnly, CancellationToken ct = default)
    {
        var docs = await db.FxDocuments.AsNoTracking().Where(d => !openOnly || !d.Settled).OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).Take(500).ToListAsync(ct);
        var ids = docs.Select(d => d.Id).ToList();
        var settlements = await db.FxSettlements.AsNoTracking().Where(s => ids.Contains(s.DocumentId)).OrderBy(s => s.Date).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var customers = await db.Customers.AsNoTracking().Where(c => docs.Select(d => d.CustomerId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return docs.Select(d => new FxDocumentDto(d.Id, d.Number, d.Kind,
            d.SupplierId is int s ? suppliers.GetValueOrDefault(s, "") : d.CustomerId is int c ? customers.GetValueOrDefault(c, "") : "",
            d.Currency, d.Date, d.DueDate, d.Description, d.ForeignAmount, d.Rate, d.BaseAmount, d.ForeignAmount - d.ForeignSettled, d.Settled,
            settlements.Where(x => x.DocumentId == d.Id).Select(x => new FxSettlementDto(x.Date, x.ForeignAmount, x.Rate, x.BaseAmount, x.GainLoss)).ToList())).ToList();
    }

    public Task<int> CreateDocumentAsync(NewFxDocumentInput i, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            if (i.Kind is not (FxDocumentKinds.SupplierBill or FxDocumentKinds.CustomerInvoice)) throw new BusinessRuleException("Choose a supplier bill or a customer invoice.");
            if (i.ForeignAmount <= 0) throw new BusinessRuleException("Enter the amount.");
            if (string.IsNullOrWhiteSpace(i.Description)) throw new BusinessRuleException("Describe the bill or invoice.");
            if (!await db.Currencies.AnyAsync(c => c.Code == i.Currency && c.Active, inner)) throw new BusinessRuleException("Choose a currency that is set up.");
            await gl.AccountsAsync(inner);   // a new business's chart is created on first use
            var account = await db.GlAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Code == i.AccountCode, inner) ?? throw new BusinessRuleException("Choose the account it belongs to.");
            if (account.IsControl && account.Code != GlCodes.GoodsInTransit) throw new BusinessRuleException($"{account.Name} is posted by the system only.");
            var rate = i.Rate is > 0 ? i.Rate.Value : await RateOnAsync(i.Currency, i.Date, inner);
            var baseAmount = R(i.ForeignAmount * rate);
            var bill = i.Kind == FxDocumentKinds.SupplierBill;
            var n = await db.FxDocuments.CountAsync(d => d.Kind == i.Kind, inner) + 1;
            var d = new FxDocument
            {
                Number = (bill ? "FXB-" : "FXI-") + n.ToString("00000"), Kind = i.Kind, Currency = i.Currency, Date = i.Date, DueDate = i.DueDate, Description = i.Description.Trim(),
                AccountCode = account.Code, ForeignAmount = R(i.ForeignAmount), Rate = rate, BaseAmount = baseAmount, CreatedByUserId = user.Id, CreatedAt = clock.UtcNow,
            };
            if (bill)
            {
                var s = await db.Suppliers.FromSqlInterpolated($"SELECT * FROM suppliers WHERE Id = {i.PartyId} FOR UPDATE").SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Supplier");
                d.SupplierId = s.Id; s.Balance += baseAmount;
                db.Ledger.Add(new LedgerEntry { EntryDate = i.Date, AccountType = LedgerAccountTypes.Supplier, AccountName = s.Name, SupplierId = s.Id, EntryType = LedgerEntryTypes.Credit, Amount = baseAmount, Reference = d.Number });
            }
            else
            {
                var c = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {i.PartyId} FOR UPDATE").SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Customer");
                d.CustomerId = c.Id; c.Balance += baseAmount;
                db.Ledger.Add(new LedgerEntry { EntryDate = i.Date, AccountType = LedgerAccountTypes.Customer, AccountName = c.Name, CustomerId = c.Id, EntryType = LedgerEntryTypes.Debit, Amount = baseAmount, Reference = d.Number });
            }
            db.FxDocuments.Add(d);
            await db.SaveChangesAsync(inner);
            var memo = $"{d.Number} {d.Currency} {d.ForeignAmount:N2} @ ₦{rate:N4}: {d.Description}";
            await gl.PostAsync("FxDocument", d.Id.ToString(), "booked", d.Date, memo,
                bill ? [PostLine.Dr(d.AccountCode, baseAmount), PostLine.Cr(GlCodes.Payables, baseAmount, supplierId: d.SupplierId)]
                     : [PostLine.Dr(GlCodes.Receivables, baseAmount, customerId: d.CustomerId), PostLine.Cr(d.AccountCode, baseAmount)], user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "FX_DOCUMENT_BOOKED", Entity = "FxDocument", EntityId = d.Id.ToString(), At = clock.UtcNow, Detail = memo });
            await db.SaveChangesAsync(inner);
            return d.Id;
        }, ct);

    /// <summary>Pays (a bill) or receives (an invoice) part or all of a foreign document at today's rate; the rate difference is a realised gain or loss.</summary>
    public Task<int> SettleAsync(int documentId, SettleInput i, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var d = await db.FxDocuments.FromSqlInterpolated($"SELECT * FROM fx_documents WHERE Id = {documentId} FOR UPDATE").SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Document");
            var outstanding = d.ForeignAmount - d.ForeignSettled;
            if (i.ForeignAmount <= 0 || i.ForeignAmount > outstanding) throw new BusinessRuleException($"Enter up to {d.Currency} {outstanding:N2}.");
            await gl.AccountsAsync(inner);
            var bank = string.IsNullOrWhiteSpace(i.BankCode) ? GlCodes.Bank : i.BankCode.Trim();
            if (!await db.GlAccounts.AnyAsync(a => a.Code == bank && a.Type == GlAccountTypes.Asset, inner)) throw new BusinessRuleException("Choose the bank or cash account.");
            var rate = i.Rate is > 0 ? i.Rate.Value : await RateOnAsync(d.Currency, i.Date, inner);
            var moved = R(i.ForeignAmount * rate);
            var carrying = i.ForeignAmount == outstanding ? d.BaseAmount - R(d.ForeignSettled * d.Rate) : R(i.ForeignAmount * d.Rate);
            var bill = d.Kind == FxDocumentKinds.SupplierBill;
            // A bill paid at a higher rate costs more naira (loss); an invoice received at a higher rate brings more (gain).
            var gain = bill ? carrying - moved : moved - carrying;
            var s = new FxSettlement { DocumentId = d.Id, Date = i.Date, ForeignAmount = R(i.ForeignAmount), Rate = rate, BaseAmount = moved, CarryingAmount = carrying, GainLoss = gain, BankCode = bank };
            db.FxSettlements.Add(s);
            d.ForeignSettled += R(i.ForeignAmount);
            d.Settled = d.ForeignSettled >= d.ForeignAmount;
            if (bill)
            {
                var sup = await db.Suppliers.FromSqlInterpolated($"SELECT * FROM suppliers WHERE Id = {d.SupplierId} FOR UPDATE").SingleAsync(inner);
                sup.Balance -= carrying;
                db.Ledger.Add(new LedgerEntry { EntryDate = i.Date, AccountType = LedgerAccountTypes.Supplier, AccountName = sup.Name, SupplierId = sup.Id, EntryType = LedgerEntryTypes.Debit, Amount = carrying, Reference = d.Number });
            }
            else
            {
                var cus = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE Id = {d.CustomerId} FOR UPDATE").SingleAsync(inner);
                cus.Balance -= carrying;
                db.Ledger.Add(new LedgerEntry { EntryDate = i.Date, AccountType = LedgerAccountTypes.Customer, AccountName = cus.Name, CustomerId = cus.Id, EntryType = LedgerEntryTypes.Credit, Amount = carrying, Reference = d.Number });
            }
            await db.SaveChangesAsync(inner);
            var fx = gain >= 0 ? PostLine.Cr(GlCodes.FxGainLoss, gain, note: "Realised exchange gain") : PostLine.Dr(GlCodes.FxGainLoss, -gain, note: "Realised exchange loss");
            await gl.PostAsync("FxSettlement", s.Id.ToString(), "settled", i.Date, $"{(bill ? "Paid" : "Received")} {d.Currency} {s.ForeignAmount:N2} on {d.Number} @ ₦{rate:N4}",
                bill ? [PostLine.Dr(GlCodes.Payables, carrying, supplierId: d.SupplierId), PostLine.Cr(bank, moved), fx]
                     : [PostLine.Dr(bank, moved), PostLine.Cr(GlCodes.Receivables, carrying, customerId: d.CustomerId), fx], user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "FX_SETTLED", Entity = "FxDocument", EntityId = d.Id.ToString(), At = clock.UtcNow,
                Detail = $"{d.Number}: {d.Currency} {s.ForeignAmount:N2} @ {rate:N4}, {(gain >= 0 ? "gain" : "loss")} ₦{Math.Abs(gain):N2}" });
            await db.SaveChangesAsync(inner);
            return s.Id;
        }, ct);

    // ---------------------------------------------------------------- revaluation
    public async Task<RevaluationDto> RevaluationAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var open = (await DocumentsAsync(true, ct)).Where(d => d.Date <= asOf).ToList();
        var rows = new List<RevaluationRow>();
        foreach (var d in open)
        {
            var closing = await RateOnAsync(d.Currency, asOf, ct);
            var diff = R(d.ForeignOutstanding * (closing - d.Rate));
            rows.Add(new RevaluationRow(d.Number, d.Kind, d.Party, d.Currency, d.ForeignOutstanding, d.Rate, closing, d.Kind == FxDocumentKinds.SupplierBill ? -diff : diff));
        }
        var posted = await db.GlJournals.AnyAsync(j => j.SourceType == "FxRevaluation" && j.SourceId == asOf.ToString("yyyy-MM-dd") && j.Status == GlJournalStatuses.Posted, ct);
        return new RevaluationDto(asOf, rows, rows.Sum(r => r.Unrealised), posted);
    }

    /// <summary>
    /// Posts the unrealised gain or loss at a month end against the payables and receivables control accounts, and reverses it the next day, so
    /// the period's statements show balances at the closing rate while settlements still realise against the original rate.
    /// </summary>
    public Task PostRevaluationAsync(DateOnly asOf, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            var r = await RevaluationAsync(asOf, inner);
            if (r.Posted) throw new BusinessRuleException("This revaluation has already been posted.");
            var bills = r.Rows.Where(x => x.Kind == FxDocumentKinds.SupplierBill).Sum(x => x.Unrealised);     // positive = gain (liability fell)
            var invoices = r.Rows.Where(x => x.Kind == FxDocumentKinds.CustomerInvoice).Sum(x => x.Unrealised);
            if (bills == 0 && invoices == 0) throw new BusinessRuleException("Nothing to revalue.");
            var lines = new List<PostLine>();
            if (bills != 0) lines.Add(bills > 0 ? PostLine.Dr(GlCodes.FxRevaluation, bills) : PostLine.Cr(GlCodes.FxRevaluation, -bills));
            if (invoices != 0) lines.Add(invoices > 0 ? PostLine.Dr(GlCodes.FxRevaluation, invoices) : PostLine.Cr(GlCodes.FxRevaluation, -invoices));
            var total = bills + invoices;
            lines.Add(total >= 0 ? PostLine.Cr(GlCodes.FxGainLoss, total, note: "Unrealised exchange gain") : PostLine.Dr(GlCodes.FxGainLoss, -total, note: "Unrealised exchange loss"));
            var key = asOf.ToString("yyyy-MM-dd");
            await gl.PostAsync("FxRevaluation", key, "revalue", asOf, $"Foreign balances revalued at {asOf:dd MMM yyyy}", PurchasingMerge(lines), user, inner);
            await db.SaveChangesAsync(inner);
            await gl.ReverseAsync("FxRevaluation", key, "revalue", "reverse", asOf.AddDays(1), $"Reversal of revaluation at {asOf:dd MMM yyyy}", user, inner);
            db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "FX_REVALUED", Entity = "FxRevaluation", EntityId = key, At = clock.UtcNow, Detail = $"₦{total:N2}" });
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    private static IEnumerable<PostLine> PurchasingMerge(IEnumerable<PostLine> lines) => Ledger.GeneralLedger.Merge(lines);
}
