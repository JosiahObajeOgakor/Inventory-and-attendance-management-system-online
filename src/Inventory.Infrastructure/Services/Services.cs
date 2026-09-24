using Inventory.Application.Abstractions;
using MySql.Data.MySqlClient;

namespace Inventory.Infrastructure.Services;

/// <summary>UTC for storage; Africa/Lagos (UTC+1, no DST) for "today" and document numbers (DISCOVERY D15).</summary>
public sealed class SystemClock : IClock
{
    private static readonly TimeSpan Lagos = TimeSpan.FromHours(1);
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime BusinessNow => DateTime.UtcNow + Lagos;
    public DateOnly BusinessToday => DateOnly.FromDateTime(BusinessNow);
}

public sealed record BankDefault(string Bank, string AccountName, string AccountNumber);

public sealed record CompanyInfo(string Key, string DisplayName, string DocumentPrefix, string Schema, string LegalName = "", bool HasPriceLists = false,
    bool BuysGoods = false, List<BankDefault>? Banks = null, int DefaultWarehouseId = 0);

public sealed class CompanyContext(CompanyInfo info) : ICompanyContext
{
    public string Key => info.Key;
    public string DocumentPrefix => info.DocumentPrefix;
    public string LegalName => string.IsNullOrWhiteSpace(info.LegalName) ? info.DisplayName : info.LegalName;
    public bool HasPriceLists => info.HasPriceLists;
    public bool BuysGoods => info.BuysGoods;
    public int DefaultWarehouseId => info.DefaultWarehouseId;
    public IReadOnlyList<(string Bank, string AccountName, string AccountNumber)> DefaultBanks =>
        (info.Banks ?? []).Select(b => (b.Bank, b.AccountName, b.AccountNumber)).ToList();
}

public sealed class MySqlErrorClassifier : IDbErrorClassifier
{
    public bool IsDeadlock(Exception ex) => Has(ex, 1213, 1205);     // deadlock, lock wait timeout
    public bool IsDuplicateKey(Exception ex) => Has(ex, 1062);
    public bool IsForeignKeyViolation(Exception ex) => Has(ex, 1451, 1452);

    private static bool Has(Exception ex, params int[] numbers)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is MySqlException m && numbers.Contains(m.Number)) return true;
        return false;
    }
}
