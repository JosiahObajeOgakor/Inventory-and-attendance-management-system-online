namespace Inventory.Domain.Entities;

/// <summary>A currency the business deals in besides naira (the base currency every ledger amount is kept in).</summary>
public class Currency
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Symbol { get; set; } = "";
    public bool Active { get; set; } = true;
}

/// <summary>Naira per one unit of a currency, from a date until the next rate.</summary>
public class ExchangeRate
{
    public int Id { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Rate { get; set; }
    public string? Source { get; set; }
}

public static class FxDocumentKinds
{
    public const string SupplierBill = "SupplierBill";
    public const string CustomerInvoice = "CustomerInvoice";
}

/// <summary>
/// A bill from a foreign supplier or an invoice to a foreign customer, in their currency. Booked in naira at the day's rate; settling it at a
/// different rate makes a realised exchange gain or loss.
/// </summary>
public class FxDocument
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Kind { get; set; } = FxDocumentKinds.SupplierBill;
    public int? SupplierId { get; set; }
    public int? CustomerId { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly Date { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Description { get; set; } = "";
    /// <summary>The other side of the posting: an expense or asset for a bill (e.g. goods in transit), an income account for an invoice.</summary>
    public string AccountCode { get; set; } = "";
    public decimal ForeignAmount { get; set; }
    public decimal Rate { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal ForeignSettled { get; set; }
    public bool Settled { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class FxSettlement
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public DateOnly Date { get; set; }
    public decimal ForeignAmount { get; set; }
    public decimal Rate { get; set; }
    /// <summary>Naira that actually moved (foreign × today's rate).</summary>
    public decimal BaseAmount { get; set; }
    /// <summary>The document's naira value for this part (foreign × the document's rate).</summary>
    public decimal CarryingAmount { get; set; }
    /// <summary>Positive = gain.</summary>
    public decimal GainLoss { get; set; }
    public string BankCode { get; set; } = GlCodes.Bank;
}
