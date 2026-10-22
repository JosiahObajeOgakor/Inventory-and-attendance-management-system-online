namespace Inventory.Domain.Entities;

/// <summary>A bank account the business holds, kept in the ledger under its own asset account (1010 is the default "Bank").</summary>
public class BankAccount
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BankName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string GlCode { get; set; } = GlCodes.Bank;
    public string Currency { get; set; } = "NGN";
    public bool Active { get; set; } = true;
}

public static class StatementLineStatuses
{
    public const string Unmatched = "Unmatched";
    public const string Matched = "Matched";
    /// <summary>Not in the books yet (bank charges, interest): an entry was posted from the statement.</summary>
    public const string Posted = "Posted";
    public const string Ignored = "Ignored";
}

/// <summary>One line of an imported bank statement. Positive = money in, negative = money out.</summary>
public class BankStatementLine
{
    public long Id { get; set; }
    public int BankAccountId { get; set; }
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
    public string? Reference { get; set; }
    public decimal Amount { get; set; }
    public decimal? RunningBalance { get; set; }
    public string Status { get; set; } = StatementLineStatuses.Unmatched;
    /// <summary>The ledger line it was matched to (one statement line clears one ledger line).</summary>
    public long? GlLineId { get; set; }
    public int? ReconciliationId { get; set; }
    public string ImportBatch { get; set; } = "";
    /// <summary>Same date, amount and description as an earlier import: kept out of matching so a statement imported twice isn't counted twice.</summary>
    public string Fingerprint { get; set; } = "";
}

public static class ReconciliationStatuses
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
}

/// <summary>Reconciling one bank account up to a statement date: the statement balance agreed to the books, with what is still in transit.</summary>
public class BankReconciliation
{
    public int Id { get; set; }
    public int BankAccountId { get; set; }
    public DateOnly StatementDate { get; set; }
    public decimal StatementBalance { get; set; }
    public decimal LedgerBalance { get; set; }
    public decimal UnclearedReceipts { get; set; }
    public decimal UnclearedPayments { get; set; }
    public string Status { get; set; } = ReconciliationStatuses.InProgress;
    public int? CompletedByUserId { get; set; }
    public string? CompletedByName { get; set; }
    public DateTime? CompletedAt { get; set; }
}
