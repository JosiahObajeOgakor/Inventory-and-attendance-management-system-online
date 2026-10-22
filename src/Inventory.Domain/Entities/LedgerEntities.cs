namespace Inventory.Domain.Entities;

public static class GlAccountTypes
{
    public const string Asset = "Asset";
    public const string Liability = "Liability";
    public const string Equity = "Equity";
    public const string Income = "Income";
    public const string Expense = "Expense";
    public static readonly string[] All = [Asset, Liability, Equity, Income, Expense];
    /// <summary>Assets and expenses grow with debits; liabilities, equity and income with credits.</summary>
    public static bool DebitNormal(string type) => type is Asset or Expense;
}

/// <summary>One account in the chart. Codes are the business's own; the posting rules use the standard codes in <see cref="GlCodes"/>.</summary>
public class GlAccount
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = GlAccountTypes.Asset;
    /// <summary>Control accounts (receivables, payables, inventory) are only posted to by the system, never by a manual journal.</summary>
    public bool IsControl { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class GlPeriodStatuses
{
    public const string Open = "Open";
    /// <summary>Only finance users with gl.period.close may post.</summary>
    public const string SoftClosed = "SoftClosed";
    /// <summary>Nobody may post. Reopening needs gl.period.close and a reason.</summary>
    public const string Locked = "Locked";
}

/// <summary>A calendar month of the books. A month with no row is Open.</summary>
public class GlPeriod
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Status { get; set; } = GlPeriodStatuses.Open;
    public DateTime? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    public string? Note { get; set; }
}

public static class GlJournalStatuses
{
    /// <summary>A manual journal waiting for a second person.</summary>
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    /// <summary>Cancelled by a later journal that posts the exact opposite lines (see ReversalOfId).</summary>
    public const string Reversed = "Reversed";
}

/// <summary>
/// One balanced posting. Never edited or deleted once posted: a correction is a new journal with ReversalOfId set. (SourceType, SourceId, SourceEvent)
/// is unique, so the same business event can never be posted twice, however often it is replayed.
/// </summary>
public class GlJournal
{
    public long Id { get; set; }
    public DateOnly Date { get; set; }
    public string SourceType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceEvent { get; set; } = "";
    public string Memo { get; set; } = "";
    public string Status { get; set; } = GlJournalStatuses.Posted;
    public long? ReversalOfId { get; set; }
    /// <summary>The branch it happened at (null = head office / not branch-specific).</summary>
    public int? BranchId { get; set; }
    public int? PreparedByUserId { get; set; }
    public string PreparedByName { get; set; } = "";
    public int? ApprovedByUserId { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<GlLine> Lines { get; set; } = [];
}

public class GlLine
{
    public long Id { get; set; }
    public long JournalId { get; set; }
    public int AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }
    public int? ProductId { get; set; }
    public string? Note { get; set; }
}

/// <summary>The standard chart of accounts. Posting rules refer to these codes.</summary>
public static class GlCodes
{
    public const string Cash = "1000";
    public const string Bank = "1010";
    public const string AlatPayClearing = "1020";
    public const string Receivables = "1100";
    public const string Inventory = "1200";
    public const string GoodsInTransit = "1220";
    public const string VatInput = "1300";
    public const string StaffLoans = "1450";
    public const string FixedAssets = "1500";
    public const string AccumulatedDepreciation = "1590";
    public const string Payables = "2000";
    public const string VatOutput = "2100";
    public const string WhtPayable = "2110";
    public const string PayePayable = "2120";
    public const string PensionPayable = "2130";
    public const string NhfPayable = "2140";
    public const string NhisPayable = "2150";
    public const string NsitfPayable = "2160";
    public const string ItfPayable = "2170";
    public const string NetSalariesPayable = "2180";
    public const string PayrollDeductionsPayable = "2190";
    public const string FxRevaluation = "2195";
    public const string CustomerDeposits = "2200";
    public const string AccruedRebates = "2300";
    public const string OwnersEquity = "3000";
    public const string RetainedEarnings = "3100";
    public const string SalesRevenue = "4000";
    public const string SalesReturns = "4100";
    public const string Rebates = "4200";
    public const string OtherIncome = "4900";
    public const string CostOfGoodsSold = "5000";
    public const string Shrinkage = "5100";
    public const string InventoryAdjustments = "5110";
    public const string ProductionInputs = "5150";
    public const string ProductionAbsorbed = "5200";
    public const string Salaries = "6000";
    public const string EmployerPension = "6010";
    public const string StatutoryLevies = "6020";
    public const string EmployerHealthInsurance = "6030";
    public const string Rent = "6100";
    public const string Utilities = "6200";
    public const string Logistics = "6300";
    public const string ProcessingFees = "6400";
    public const string Depreciation = "6500";
    public const string Maintenance = "6600";
    public const string OtherExpenses = "6900";
    public const string FxGainLoss = "7000";
    public const string Suspense = "9999";

    public static readonly (string Code, string Name, string Type, bool Control)[] Standard =
    [
        (Cash, "Cash on hand", GlAccountTypes.Asset, false),
        (Bank, "Bank", GlAccountTypes.Asset, false),
        (AlatPayClearing, "ALATPay clearing", GlAccountTypes.Asset, true),
        (Receivables, "Accounts receivable", GlAccountTypes.Asset, true),
        (Inventory, "Inventory", GlAccountTypes.Asset, true),
        (GoodsInTransit, "Goods ordered, not yet received", GlAccountTypes.Asset, true),
        (VatInput, "VAT input (recoverable)", GlAccountTypes.Asset, true),
        (StaffLoans, "Staff loans receivable", GlAccountTypes.Asset, true),
        (FixedAssets, "Fixed assets", GlAccountTypes.Asset, false),
        (AccumulatedDepreciation, "Accumulated depreciation", GlAccountTypes.Asset, false),
        (Payables, "Accounts payable", GlAccountTypes.Liability, true),
        (VatOutput, "VAT output", GlAccountTypes.Liability, true),
        (WhtPayable, "Withholding tax payable", GlAccountTypes.Liability, false),
        (PayePayable, "PAYE payable", GlAccountTypes.Liability, false),
        (PensionPayable, "Pension payable", GlAccountTypes.Liability, false),
        (NhfPayable, "NHF payable", GlAccountTypes.Liability, false),
        (NhisPayable, "NHIS payable", GlAccountTypes.Liability, false),
        (NsitfPayable, "NSITF payable", GlAccountTypes.Liability, false),
        (ItfPayable, "ITF payable", GlAccountTypes.Liability, false),
        (NetSalariesPayable, "Net salaries payable", GlAccountTypes.Liability, true),
        (PayrollDeductionsPayable, "Other payroll deductions payable", GlAccountTypes.Liability, false),
        (FxRevaluation, "Foreign-currency revaluation adjustment", GlAccountTypes.Liability, false),
        (CustomerDeposits, "Customer deposits and credits", GlAccountTypes.Liability, true),
        (AccruedRebates, "Accrued customer rebates", GlAccountTypes.Liability, true),
        (OwnersEquity, "Owner's equity", GlAccountTypes.Equity, false),
        (RetainedEarnings, "Retained earnings", GlAccountTypes.Equity, false),
        (SalesRevenue, "Sales revenue", GlAccountTypes.Income, true),
        (SalesReturns, "Sales returns", GlAccountTypes.Income, true),
        (Rebates, "Customer rebates", GlAccountTypes.Income, true),
        (OtherIncome, "Other income", GlAccountTypes.Income, false),
        (CostOfGoodsSold, "Cost of goods sold", GlAccountTypes.Expense, true),
        (Shrinkage, "Stock shrinkage and write-offs", GlAccountTypes.Expense, true),
        (InventoryAdjustments, "Inventory cost adjustments", GlAccountTypes.Expense, true),
        (ProductionInputs, "Raw materials and production costs", GlAccountTypes.Expense, false),
        (ProductionAbsorbed, "Production costs absorbed", GlAccountTypes.Expense, true),
        (Salaries, "Salaries", GlAccountTypes.Expense, false),
        (EmployerPension, "Employer pension contributions", GlAccountTypes.Expense, false),
        (StatutoryLevies, "Statutory payroll levies (NSITF, ITF)", GlAccountTypes.Expense, false),
        (EmployerHealthInsurance, "Employer health insurance (NHIS)", GlAccountTypes.Expense, false),
        (Rent, "Rent", GlAccountTypes.Expense, false),
        (Utilities, "Utilities", GlAccountTypes.Expense, false),
        (Logistics, "Logistics", GlAccountTypes.Expense, false),
        (ProcessingFees, "Payment processing fees", GlAccountTypes.Expense, false),
        (Depreciation, "Depreciation", GlAccountTypes.Expense, false),
        (Maintenance, "Maintenance", GlAccountTypes.Expense, false),
        (OtherExpenses, "Other expenses", GlAccountTypes.Expense, false),
        (FxGainLoss, "Foreign exchange gain/loss", GlAccountTypes.Expense, false),
        (Suspense, "Suspense (unmapped postings)", GlAccountTypes.Asset, false),
    ];

    /// <summary>Where money received or paid by this method sits.</summary>
    public static string ForPaymentMethod(string? method) => method switch
    {
        "Cash" => Cash,
        "ALATPay" => AlatPayClearing,
        _ => Bank,   // Bank Transfer, Card (POS terminal settles to the bank), and anything else
    };

    /// <summary>The expense account for an expense category (the categories the app already uses).</summary>
    public static string ForExpenseCategory(string? category) => category switch
    {
        "Rent" => Rent,
        "Salaries" => Salaries,
        "Utilities" => Utilities,
        "Logistics" => Logistics,
        "Maintenance" => Maintenance,
        _ => OtherExpenses,
    };
}
