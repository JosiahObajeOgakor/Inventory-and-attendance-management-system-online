using Inventory.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// One authorisation policy per permission: [Authorize(Policy = Perm.SalesCreate)]. The policy is built on demand by <see cref="PermissionPolicyProvider"/>
/// and passes when the signed-in user's cookie carries that permission claim (put there at sign-in and refreshed every minute from the user's roles).
/// </summary>
public static class Perm
{
    public const string Prefix = "perm:";
    public const string ProductsView = Prefix + Permissions.ProductsView;
    public const string ProductsCreate = Prefix + Permissions.ProductsCreate;
    public const string ProductsEdit = Prefix + Permissions.ProductsEdit;
    public const string ProductsDelete = Prefix + Permissions.ProductsDelete;
    public const string CostView = Prefix + Permissions.CostView;
    public const string CatalogSetup = Prefix + Permissions.CatalogSetup;
    public const string StockView = Prefix + Permissions.StockView;
    public const string StockReceive = Prefix + Permissions.StockReceive;
    public const string StockCorrect = Prefix + Permissions.StockCorrect;
    public const string StockDelete = Prefix + Permissions.StockDelete;
    public const string StockTransfer = Prefix + Permissions.StockTransfer;
    public const string StockAdjust = Prefix + Permissions.StockAdjust;
    public const string SerialsManage = Prefix + Permissions.SerialsManage;
    public const string SerialsWriteOff = Prefix + Permissions.SerialsWriteOff;
    public const string PriceBookView = Prefix + Permissions.PriceBookView;
    public const string PriceBookEdit = Prefix + Permissions.PriceBookEdit;
    public const string SalesView = Prefix + Permissions.SalesView;
    public const string SalesCreate = Prefix + Permissions.SalesCreate;
    public const string SalesEdit = Prefix + Permissions.SalesEdit;
    public const string SalesVoid = Prefix + Permissions.SalesVoid;
    public const string SalesDelete = Prefix + Permissions.SalesDelete;
    public const string QuotationsCreate = Prefix + Permissions.QuotationsCreate;
    public const string QuotationsDelete = Prefix + Permissions.QuotationsDelete;
    public const string WaybillsManage = Prefix + Permissions.WaybillsManage;
    public const string WaybillsEdit = Prefix + Permissions.WaybillsEdit;
    public const string CustomersView = Prefix + Permissions.CustomersView;
    public const string CustomersCreate = Prefix + Permissions.CustomersCreate;
    public const string CustomersEdit = Prefix + Permissions.CustomersEdit;
    public const string CustomersDelete = Prefix + Permissions.CustomersDelete;
    public const string CustomerPayments = Prefix + Permissions.CustomerPayments;
    public const string RebatesManage = Prefix + Permissions.RebatesManage;
    public const string PartnersManage = Prefix + Permissions.PartnersManage;
    public const string SuppliersManage = Prefix + Permissions.SuppliersManage;
    public const string SuppliersDelete = Prefix + Permissions.SuppliersDelete;
    public const string PurchasesView = Prefix + Permissions.PurchasesView;
    public const string PurchasesCreate = Prefix + Permissions.PurchasesCreate;
    public const string PurchasesEdit = Prefix + Permissions.PurchasesEdit;
    public const string PurchasesReceive = Prefix + Permissions.PurchasesReceive;
    public const string PurchasesPay = Prefix + Permissions.PurchasesPay;
    public const string PurchasesDelete = Prefix + Permissions.PurchasesDelete;
    public const string FinanceView = Prefix + Permissions.FinanceView;
    public const string ExpensesManage = Prefix + Permissions.ExpensesManage;
    public const string ExpensesDelete = Prefix + Permissions.ExpensesDelete;
    public const string GlView = Prefix + Permissions.GlView;
    public const string GlJournalCreate = Prefix + Permissions.GlJournalCreate;
    public const string GlJournalApprove = Prefix + Permissions.GlJournalApprove;
    public const string GlPeriodClose = Prefix + Permissions.GlPeriodClose;
    public const string TaxManage = Prefix + Permissions.TaxManage;
    public const string BankReconcile = Prefix + Permissions.BankReconcile;
    public const string ApprovalsDecide = Prefix + Permissions.ApprovalsDecide;
    public const string AttendanceSelf = Prefix + Permissions.AttendanceSelf;
    public const string AttendanceView = Prefix + Permissions.AttendanceView;
    public const string EmployeesManage = Prefix + Permissions.EmployeesManage;
    public const string EmployeesDelete = Prefix + Permissions.EmployeesDelete;
    public const string PayrollManage = Prefix + Permissions.PayrollManage;
    public const string PayrollDelete = Prefix + Permissions.PayrollDelete;
    public const string TimeOffManage = Prefix + Permissions.TimeOffManage;
    public const string CampaignsManage = Prefix + Permissions.CampaignsManage;
    public const string CampaignsRun = Prefix + Permissions.CampaignsRun;
    public const string SiteContentManage = Prefix + Permissions.SiteContentManage;
    public const string MarketingPublish = Prefix + Permissions.MarketingPublish;
    public const string MarketingApprove = Prefix + Permissions.MarketingApprove;
    public const string SocialManage = Prefix + Permissions.SocialManage;
    public const string EmailMarketingManage = Prefix + Permissions.EmailMarketingManage;
    public const string SmsManage = Prefix + Permissions.SmsManage;
    public const string WhatsAppManage = Prefix + Permissions.WhatsAppManage;
    public const string AutomationManage = Prefix + Permissions.AutomationManage;
    public const string SurveysManage = Prefix + Permissions.SurveysManage;
    public const string BlogManage = Prefix + Permissions.BlogManage;
    public const string SignManage = Prefix + Permissions.SignManage;
    public const string SpreadsheetManage = Prefix + Permissions.SpreadsheetManage;
    public const string EsgManage = Prefix + Permissions.EsgManage;
    public const string RecruitmentManage = Prefix + Permissions.RecruitmentManage;
    public const string AppraisalsManage = Prefix + Permissions.AppraisalsManage;
    public const string FleetManage = Prefix + Permissions.FleetManage;
    public const string WebsiteManage = Prefix + Permissions.WebsiteManage;
    public const string EventsManage = Prefix + Permissions.EventsManage;
    public const string ElearningManage = Prefix + Permissions.ElearningManage;
    public const string ForumManage = Prefix + Permissions.ForumManage;
    public const string DocumentsView = Prefix + Permissions.DocumentsView;
    public const string DocumentsEmail = Prefix + Permissions.DocumentsEmail;
    public const string AnalyticsView = Prefix + Permissions.AnalyticsView;
    public const string AiAsk = Prefix + Permissions.AiAsk;
    public const string UsersManage = Prefix + Permissions.UsersManage;
    public const string AuditView = Prefix + Permissions.AuditView;
    public const string CompanyEdit = Prefix + Permissions.CompanyEdit;
    public const string CompanyDelete = Prefix + Permissions.CompanyDelete;
    public const string StorageManage = Prefix + Permissions.StorageManage;
}

/// <summary>Turns "perm:sales.create" into "needs the permission claim sales.create"; "perm:a|b" is satisfied by either. Unknown codes fail loudly.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Perm.Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        var codes = policyName[Perm.Prefix.Length..].Split('|');
        if (codes.FirstOrDefault(c => !Permissions.IsKnown(c)) is string bad) throw new InvalidOperationException($"Unknown permission '{bad}' in an [Authorize] attribute.");
        return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(AppClaims.Permission, codes).Build();
    }
}

/// <summary>
/// Builds the signed-in principal with one claim per permission the user's roles grant. Identity uses it at sign-in and again at every
/// security-stamp refresh (each minute), so a role the CEO edits applies to everyone in it within a minute, without signing anyone out.
/// </summary>
public sealed class AppClaimsFactory(Microsoft.AspNetCore.Identity.UserManager<Inventory.Infrastructure.Identity.AppUser> users,
    Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<int>> roles, IOptions<Microsoft.AspNetCore.Identity.IdentityOptions> options,
    Inventory.Infrastructure.Identity.IdentityStore store)
    : Microsoft.AspNetCore.Identity.UserClaimsPrincipalFactory<Inventory.Infrastructure.Identity.AppUser, Microsoft.AspNetCore.Identity.IdentityRole<int>>(users, roles, options)
{
    protected override async Task<System.Security.Claims.ClaimsIdentity> GenerateClaimsAsync(Inventory.Infrastructure.Identity.AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        foreach (var p in await store.PermissionsForAsync(user.Id)) id.AddClaim(new System.Security.Claims.Claim(AppClaims.Permission, p));
        return id;
    }
}
