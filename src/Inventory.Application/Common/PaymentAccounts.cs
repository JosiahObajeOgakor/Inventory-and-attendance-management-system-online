using Inventory.Application.Company;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Common;

/// <summary>A company bank account as it was when a payment was recorded.</summary>
public sealed record AccountRef(string BankName, string AccountName, string AccountNumber);

/// <summary>
/// "Which of our accounts did the money go into (or leave from)?" — for a bank transfer the person recording the payment picks one of the company's accounts
/// (the list on the Company &amp; documents screen), and the bank, account name and number are copied onto the payment so the record stays true even if the list is edited later.
/// The account is identified by its number, which is stable (the list's row ids are rebuilt every time the profile is saved).
/// </summary>
public sealed class PaymentAccountService(CompanyProfileService profile)
{
    public const string BankTransfer = "Bank Transfer";

    /// <summary>
    /// Null for anything that is not a bank transfer, or when no money moved. For a bank transfer with money: the chosen account, or an error if none was chosen
    /// (when the company has accounts listed) or the number is not one of them.
    /// </summary>
    public async Task<AccountRef?> ResolveAsync(string? method, string? accountNumber, decimal amount, CancellationToken ct)
    {
        if (!string.Equals(method, BankTransfer, StringComparison.OrdinalIgnoreCase) || amount <= 0) return null;
        var banks = await profile.BanksAsync(ct);
        if (banks.Count == 0) return null;                                       // no accounts listed yet: nothing to choose from
        var wanted = (accountNumber ?? "").Trim();
        if (wanted.Length == 0) throw new BusinessRuleException("Choose which of your accounts the bank transfer was made to or from.");
        var bank = banks.FirstOrDefault(b => b.AccountNumber == wanted) ?? throw new BusinessRuleException("That isn't one of your company's accounts. Choose one from the list.");
        return new AccountRef(bank.BankName, bank.AccountName, bank.AccountNumber);
    }
}

public static class PaymentAccountExtensions
{
    public static Payment With(this Payment p, AccountRef? account, string? payer)
    {
        if (account is not null) { p.BankName = account.BankName; p.AccountName = account.AccountName; p.AccountNumber = account.AccountNumber; }
        p.PayerName = string.IsNullOrWhiteSpace(payer) ? null : payer.Trim();
        return p;
    }
}
