namespace Inventory.Domain;

/// <summary>
/// Every action the API can authorise, as "module.action". Roles are editable bundles of these (stored per role in the identity schema, ticked by the CEO
/// under People &amp; access → Roles); each endpoint asks for exactly one. Codes are stable: renaming one is a data migration, not a refactor.
/// </summary>
public static class Permissions
{
    // Products & stock
    public const string ProductsView = "products.view";
    public const string ProductsCreate = "products.create";
    public const string ProductsEdit = "products.edit";
    public const string ProductsDelete = "products.delete";
    public const string CostView = "cost.view";
    public const string CatalogSetup = "catalog.setup";
    public const string StockView = "stock.view";
    public const string StockReceive = "stock.receive";
    public const string StockCorrect = "stock.correct";
    public const string StockDelete = "stock.delete";
    public const string StockTransfer = "stock.transfer";
    public const string StockAdjust = "stock.adjust";
    public const string SerialsManage = "serials.manage";
    public const string SerialsWriteOff = "serials.writeoff";
    public const string PriceBookView = "pricebook.view";
    public const string PriceBookEdit = "pricebook.edit";
    // Sales & customers
    public const string SalesView = "sales.view";
    public const string SalesCreate = "sales.create";
    public const string SalesEdit = "sales.edit";
    public const string SalesVoid = "sales.void";
    public const string SalesDelete = "sales.delete";
    public const string QuotationsCreate = "quotations.create";
    public const string QuotationsDelete = "quotations.delete";
    public const string WaybillsManage = "waybills.manage";
    public const string WaybillsEdit = "waybills.edit";
    public const string CustomersView = "customers.view";
    public const string CustomersCreate = "customers.create";
    public const string CustomersEdit = "customers.edit";
    public const string CustomersDelete = "customers.delete";
    public const string CustomerPayments = "customers.payments";
    public const string RebatesManage = "rebates.manage";
    public const string PartnersManage = "partners.manage";
    // Purchasing
    public const string SuppliersManage = "suppliers.manage";
    public const string SuppliersDelete = "suppliers.delete";
    public const string PurchasesView = "purchases.view";
    public const string PurchasesCreate = "purchases.create";
    public const string PurchasesEdit = "purchases.edit";
    public const string PurchasesReceive = "purchases.receive";
    public const string PurchasesPay = "purchases.pay";
    public const string PurchasesDelete = "purchases.delete";
    // Money
    public const string FinanceView = "finance.view";
    public const string ExpensesManage = "expenses.manage";
    public const string ExpensesDelete = "expenses.delete";
    public const string GlView = "gl.view";
    public const string GlJournalCreate = "gl.journal.create";
    public const string GlJournalApprove = "gl.journal.approve";
    public const string GlPeriodClose = "gl.period.close";
    public const string TaxManage = "tax.manage";
    public const string BankReconcile = "bank.reconcile";
    public const string ApprovalsDecide = "approvals.decide";
    // People
    public const string AttendanceSelf = "attendance.self";
    public const string AttendanceView = "attendance.view";
    public const string EmployeesManage = "employees.manage";
    public const string EmployeesDelete = "employees.delete";
    public const string PayrollManage = "payroll.manage";
    public const string PayrollDelete = "payroll.delete";
    public const string TimeOffManage = "timeoff.manage";
    // Marketing
    public const string CampaignsManage = "campaigns.manage";
    public const string CampaignsRun = "campaigns.run";
    public const string SiteContentManage = "sitecontent.manage";
    public const string MarketingPublish = "marketing.publish";
    public const string MarketingApprove = "marketing.approve";
    public const string SocialManage = "social.manage";
    public const string EmailMarketingManage = "emailmarketing.manage";
    public const string SmsManage = "sms.manage";
    public const string WhatsAppManage = "whatsapp.manage";
    public const string AutomationManage = "automation.manage";
    public const string SurveysManage = "surveys.manage";
    public const string BlogManage = "blog.manage";
    // The other record-keeping apps: one permission each, "everything in this app".
    public const string SignManage = "sign.manage";
    public const string SpreadsheetManage = "spreadsheet.manage";
    public const string EsgManage = "esg.manage";
    public const string RecruitmentManage = "recruitment.manage";
    public const string AppraisalsManage = "appraisals.manage";
    public const string FleetManage = "fleet.manage";
    public const string WebsiteManage = "website.manage";
    public const string EventsManage = "events.manage";
    public const string ElearningManage = "elearning.manage";
    public const string ForumManage = "forum.manage";
    // Documents, analytics, AI
    public const string DocumentsView = "documents.view";
    public const string DocumentsEmail = "documents.email";
    public const string AnalyticsView = "analytics.view";
    public const string AiAsk = "ai.ask";
    // Administration
    public const string UsersManage = "users.manage";
    public const string AuditView = "audit.view";
    public const string CompanyEdit = "company.edit";
    public const string CompanyDelete = "company.delete";
    public const string StorageManage = "storage.manage";

    public static readonly IReadOnlyList<string> All = typeof(Permissions).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();

    public static bool IsKnown(string code) => All.Contains(code);

    /// <summary>The permission behind each record-keeping app (by app key).</summary>
    public static readonly IReadOnlyDictionary<string, string> ForApp = new Dictionary<string, string>
    {
        ["sign"] = SignManage, ["spreadsheet"] = SpreadsheetManage, ["esg"] = EsgManage, ["time-off"] = TimeOffManage, ["recruitment"] = RecruitmentManage,
        ["appraisals"] = AppraisalsManage, ["fleet"] = FleetManage, ["website"] = WebsiteManage, ["events"] = EventsManage, ["blog"] = BlogManage,
        ["elearning"] = ElearningManage, ["forum"] = ForumManage, ["social-marketing"] = SocialManage, ["surveys"] = SurveysManage,
        ["email-marketing"] = EmailMarketingManage, ["sms-marketing"] = SmsManage, ["whatsapp"] = WhatsAppManage, ["marketing-automation"] = AutomationManage,
    };
}

/// <summary>
/// What each built-in role starts with: exactly what it could reach before roles became editable, so nobody gained or lost anything on the day this shipped.
/// A role is seeded once; after that it belongs to the CEO, who ticks and unticks under People &amp; access → Roles. ADMIN always has everything.
/// </summary>
public static class RoleTemplates
{
    private static readonly string[] Staff =
    [
        Permissions.ProductsView, Permissions.StockView, Permissions.StockReceive, Permissions.StockTransfer, Permissions.SerialsManage, Permissions.SalesView,
        Permissions.SalesCreate, Permissions.QuotationsCreate, Permissions.WaybillsManage, Permissions.PriceBookView, Permissions.DocumentsView, Permissions.DocumentsEmail,
        Permissions.AttendanceSelf,
    ];

    private static readonly string[] Marketing =
    [
        Permissions.CampaignsManage, Permissions.SiteContentManage, Permissions.SocialManage, Permissions.EmailMarketingManage, Permissions.SmsManage,
        Permissions.WhatsAppManage, Permissions.AutomationManage, Permissions.SurveysManage, Permissions.BlogManage,
    ];

    /// <summary>Only the CEO did these before: deleting, clearing, people and storage.</summary>
    private static readonly string[] CeoOnly =
    [
        Permissions.ProductsDelete, Permissions.CustomersDelete, Permissions.SalesVoid, Permissions.SalesDelete, Permissions.QuotationsDelete, Permissions.StockDelete,
        Permissions.SuppliersDelete, Permissions.PurchasesDelete, Permissions.ExpensesDelete, Permissions.EmployeesDelete, Permissions.PayrollDelete,
        Permissions.CompanyDelete, Permissions.UsersManage, Permissions.StorageManage,
    ];

    public static readonly IReadOnlyDictionary<string, string[]> All = new Dictionary<string, string[]>
    {
        [RoleNames.Admin] = [.. Permissions.All],
        [RoleNames.Manager] = [.. Permissions.All.Except(CeoOnly)],
        [RoleNames.Clerk] = Staff,
        [RoleNames.Marketer] = Marketing,
    };
}
