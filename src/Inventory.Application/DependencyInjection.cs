using FluentValidation;
using Inventory.Application.Common;
using Inventory.Application.Company;
using Inventory.Application.Documents;
using Inventory.Application.Products;
using Inventory.Application.Queries;
using Inventory.Application.Purchasing;
using Inventory.Application.Sales;
using Inventory.Application.Stock;
using Inventory.Application.Staff;
using Inventory.Application.Trade;
using Inventory.Application.Finance;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection s)
    {
        s.AddValidatorsFromAssemblyContaining<SaleRequestValidator>();
        s.AddScoped<TransactionRunner>();
        s.AddScoped<PaymentAccountService>();
        s.AddScoped<StockService>();
        s.AddScoped<SalesService>();
        s.AddScoped<CustomerPaymentService>();
        s.AddScoped<InvoiceVoidService>();
        s.AddScoped<InvoiceEditService>();
        s.AddScoped<InvoiceDeleteService>();
        s.AddScoped<PurchaseService>();
        s.AddScoped<PurchaseEditService>();
        s.AddScoped<StockOperations>();
        s.AddScoped<StockConsolidationService>();
        s.AddScoped<ProductService>();
        s.AddScoped<PartnerService>();
        s.AddScoped<CatalogQueries>();
        s.AddScoped<PartnerQueries>();
        s.AddScoped<SalesQueries>();
        s.AddScoped<PurchaseQueries>();
        s.AddScoped<FinanceQueries>();
        s.AddScoped<DashboardQueries>();
        s.AddScoped<CompanyProfileService>();
        s.AddScoped<DocumentQueries>();
        s.AddScoped<QuotationService>();
        s.AddScoped<WaybillService>();
        s.AddScoped<AttendanceService>();
        s.AddScoped<PayrollService>();
        s.AddScoped<SerialService>();
        s.AddScoped<ExpenseService>();
        s.AddScoped<RebateService>();
        s.AddScoped<PriceBookService>();
        s.AddScoped<CustomerMetricsService>();
        s.AddScoped<Dashboard.OverviewQueries>();
        s.AddScoped<Analytics.AnalyticsService>();
        s.AddScoped<Ai.AiToolbox>();
        s.AddScoped<Ai.AssistantService>();
        s.AddScoped<Ai.InsightService>();
        s.AddScoped<Email.DocumentEmailService>();
        s.AddScoped<Documents.CatalogService>();
        s.AddScoped<Documents.DocumentSendService>();
        s.AddScoped<SupplierStatementQueries>();
        s.AddScoped<SupplyService>();
        s.AddScoped<SupplyQueries>();   // its validator comes from AddValidatorsFromAssemblyContaining above
        s.AddScoped<SupplierCatalogService>();
        s.AddScoped<Analytics.ReorderDueQueries>();
        s.AddScoped<Alerts.BusinessAlertService>();
        s.AddScoped<Company.DeliveryZoneService>();
        s.AddScoped<Payments.PaymentGateways>();
        s.AddScoped<Payments.PaymentLinkService>();
        s.AddScoped<Payments.PaymentFollowUpService>();
        s.AddScoped<Payments.OrderDispatchService>();
        s.AddScoped<SalesAssistant.CustomerLookupService>();
        s.AddScoped<SalesAssistant.ChatConversationService>();
        s.AddScoped<SalesAssistant.WebhookDedupService>();
        s.AddScoped<SalesAssistant.SalesAssistantToolbox>();
        s.AddScoped<SalesAssistant.VetGuidanceService>();
        s.AddScoped<SalesAssistant.SalesAssistantService>();
        s.AddScoped<SalesAssistant.Chat.StockAvailabilityService>();
        s.AddScoped<SalesAssistant.Chat.ChatAiInterpreter>();
        s.AddScoped<SalesAssistant.Chat.ChatOrderText>();
        s.AddScoped<SalesAssistant.Chat.ChatFlowService>();
        s.AddScoped<SalesAssistant.Chat.ChatPaymentNotifier>();
        s.AddScoped<Campaigns.ConsentService>();
        s.AddScoped<Campaigns.CampaignPlanner>();
        s.AddScoped<Campaigns.CampaignWriter>();      // scoped on purpose: one scope = one run = one AI budget
        s.AddScoped<Campaigns.CampaignRunner>();
        // The record-keeping apps this business has bought (Sign, Events, Fleet…): one engine, and each app's own rules (every IAppHook in this assembly).
        s.AddScoped<AppsEngine.AppRecordService>();
        s.AddScoped<AppsEngine.FiguresService>();
        s.AddScoped<TimeOff.TimeOffService>();
        s.AddScoped<Branches.BranchService>();
        s.AddScoped<Queries.ChatInsightsQueries>();
        s.AddScoped<Queries.ChatFunnelQueries>();
        s.AddScoped<Queries.DebtorQueries>();
        s.AddScoped<Website.WebsiteService>();
        s.AddScoped<Partners.TradePartnerService>();
        s.AddScoped<Campaigns.CampaignIdeas>();
        s.AddScoped<Website.WebCheckoutService>();
        s.AddScoped<Ledger.GeneralLedger>();
        s.AddScoped<Ledger.LedgerSync>();
        s.AddScoped<Ledger.AccountingService>();
        s.AddScoped<Tax.TaxService>();
        s.AddScoped<Banking.BankReconciliationService>();
        s.AddScoped<Assets.FixedAssetService>();
        s.AddScoped<Budgets.BudgetService>();
        s.AddScoped<Currencies.CurrencyService>();
        s.AddScoped<Approvals.ApprovalService>();
        s.AddScoped<Approvals.IApprovalHandler, Approvals.StockAdjustApproval>();
        s.AddScoped<Approvals.IApprovalHandler, Approvals.SalesVoidApproval>();
        s.AddScoped<AppsEngine.BroadcastQueue>();
        s.AddScoped<AppsEngine.BroadcastDispatcher>();
        s.AddScoped<AppsEngine.AutomationService>();
        foreach (var hook in typeof(DependencyInjection).Assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(AppsEngine.IAppHook).IsAssignableFrom(t)))
            s.AddScoped(typeof(AppsEngine.IAppHook), hook);
        s.AddScoped<Campaigns.CampaignService>();
        s.AddScoped<Payments.PayPageService>();
        s.AddScoped<SalesAssistant.WhatsAppInbox>();
        s.AddScoped<SalesAssistant.WhatsAppInboxProcessor>();
        return s;
    }
}
