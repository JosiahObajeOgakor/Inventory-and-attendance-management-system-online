namespace Inventory.Domain;

public static class RoleNames
{
    /// <summary>The CEO: everything, including deleting records, clearing history and managing people and roles.</summary>
    public const string Admin = "ADMIN";
    /// <summary>Supervises clerks; adds and edits records like the CEO but cannot delete. The CEO can make a clerk manager (and back) at any time.</summary>
    public const string Manager = "MANAGER";
    public const string Clerk = "CLERK";
    public static readonly string[] All = [Admin, Manager, Clerk];
    /// <summary>Legacy role names in the SQL Server data, mapped on import.</summary>
    public const string LegacyAdmin = "Admin";
    public const string LegacyClerk = "Warehouse Clerk";
}

/// <summary>How far back records can be dated, reported and exported.</summary>
public static class BusinessDates
{
    public static readonly DateOnly Earliest = new(2020, 1, 1);
}

public static class PriceTiers
{
    public const string Distributor = "Distributor";
    public const string Wholesaler = "Wholesaler";
    public const string Retailer = "Retailer";
    public static readonly string[] All = [Distributor, Wholesaler, Retailer];
    public static bool IsValid(string? t) => t is not null && All.Contains(t);
}

public static class CustomerTypes
{
    public const string Distributor = "Distributor";
    public const string Wholesaler = "Wholesaler";
    public const string Retailer = "Retailer";
    public const string WalkIn = "Walk-in";
    public static readonly string[] All = [Distributor, Wholesaler, Retailer, WalkIn];
}

public static class PaymentStatuses
{
    public const string Paid = "Paid";
    public const string Partial = "Partial";
    public const string Unpaid = "Unpaid";
    /// <summary>New: an invoice reversed by an admin (replaces the destructive delete; defect D1).</summary>
    public const string Voided = "Voided";

    public static string For(decimal paid, decimal total) =>
        paid >= total ? Paid : paid > 0 ? Partial : Unpaid;
}

public static class PurchaseStatuses
{
    public const string Pending = "Pending";
    public const string Ordered = "Ordered";
    public const string Received = "Received";
    public const string Cancelled = "Cancelled";
}

public static class MovementTypes
{
    public const string In = "IN";
    public const string Out = "OUT";
    public const string Adjust = "ADJUST";
}

public static class MovementReferences
{
    public const string Invoice = "Invoice";
    public const string PurchaseOrder = "PurchaseOrder";
    public const string Production = "Production";
    public const string Transfer = "Transfer";
    public const string Manual = "Manual";
    /// <summary>New: opening stock entered with a product, or synthesized by the migration (defect D4).</summary>
    public const string OpeningBalance = "OpeningBalance";
    /// <summary>New: reversal of a voided invoice.</summary>
    public const string InvoiceVoid = "InvoiceVoid";
    /// <summary>Stock returned because a sale was deleted (the sale itself no longer exists; the movement keeps its old id and number).</summary>
    public const string InvoiceDelete = "InvoiceDelete";
    /// <summary>New: stock moved because a sale's lines were edited (IN = units handed back, OUT = extra units taken).</summary>
    public const string InvoiceEdit = "InvoiceEdit";
    /// <summary>New: stock moved because a received purchase's lines were edited.</summary>
    public const string PurchaseEdit = "PurchaseEdit";
}

public static class LedgerAccountTypes
{
    public const string Customer = "Customer";
    public const string Supplier = "Supplier";
}

public static class LedgerEntryTypes
{
    public const string Debit = "Debit";
    public const string Credit = "Credit";
}
