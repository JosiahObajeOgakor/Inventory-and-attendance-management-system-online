namespace Inventory.Domain.Rules;

public sealed record OpenDocument(int Id, decimal Total, decimal Paid)
{
    public decimal Owed => Total - Paid;
}

public sealed record PaymentApplication(int DocumentId, decimal Amount);

/// <summary>
/// The settle-this-first, then-oldest-first payment rule shared by customers (S6) and suppliers (P2).
/// </summary>
public static class PaymentWaterfall
{
    /// <summary>Split of a payment made together with a new document.</summary>
    public sealed record NewDocumentSplit(decimal PaidNow, decimal AppliedToNew, decimal Overflow, decimal Outstanding, string Status);

    /// <summary>
    /// <paramref name="previousBalance"/> is what was already owed. The payment is capped at
    /// previous + new total (overpayment is never stored as credit), applied to the new document first,
    /// and any overflow is available for older documents.
    /// </summary>
    public static NewDocumentSplit ForNewDocument(decimal previousBalance, decimal newTotal, decimal requestedPaid)
    {
        var combinedDue = previousBalance + newTotal;
        var paidNow = Math.Max(0m, Math.Min(requestedPaid, combinedDue));
        var applied = Math.Min(paidNow, newTotal);
        return new NewDocumentSplit(paidNow, applied, paidNow - applied, newTotal - applied,
            PaymentStatuses.For(applied, newTotal));
    }

    /// <summary>Spread <paramref name="amount"/> over open documents supplied oldest first; each capped at what it owes.</summary>
    public static (IReadOnlyList<PaymentApplication> Applications, decimal Applied) Spread(
        IEnumerable<OpenDocument> oldestFirst, decimal amount)
    {
        var result = new List<PaymentApplication>();
        if (amount <= 0) return (result, 0m);
        var remaining = amount;
        foreach (var doc in oldestFirst)
        {
            if (remaining <= 0) break;
            var owed = doc.Owed;
            if (owed <= 0) continue;
            var apply = Math.Min(owed, remaining);
            result.Add(new PaymentApplication(doc.Id, apply));
            remaining -= apply;
        }
        return (result, amount - remaining);
    }
}
