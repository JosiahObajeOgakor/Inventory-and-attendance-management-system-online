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
        s.AddScoped<StockService>();
        s.AddScoped<SalesService>();
        s.AddScoped<CustomerPaymentService>();
        s.AddScoped<InvoiceVoidService>();
        s.AddScoped<PurchaseService>();
        s.AddScoped<StockOperations>();
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
        s.AddScoped<Payments.PaymentLinkService>();
        return s;
    }
}
