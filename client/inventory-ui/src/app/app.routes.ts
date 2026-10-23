import { Routes } from '@angular/router';
import { appGuard, appPermGuard, authGuard, dashboardGuard, marketingGuard, newFeaturesGuard, permGuard } from './core/http';
import { Shell } from './layout/shell';

/** A page that needs a permission (any one of several with 'a|b'). */
const needs = (perm: string, extra: object = {}) => ({ canActivate: [permGuard], data: { perm, ...extra } });

// Every feature is lazy-loaded. The guards only shape the UI: the API checks the same permission on every endpoint.
export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/auth/login').then(m => m.Login), title: 'Sign in' },
  { path: 'paid', loadComponent: () => import('./features/auth/paid').then(m => m.PaidPage), title: 'Payment received' },
  { path: 'change-password', loadComponent: () => import('./features/auth/change-password').then(m => m.ChangePassword), title: 'Change password' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', canActivate: [dashboardGuard], loadComponent: () => import('./features/dashboard/dashboard').then(m => m.DashboardPage), title: 'Dashboard' },
      { path: 'sales', ...needs('sales.view'), loadComponent: () => import('./features/sales/sales-list').then(m => m.SalesList), title: 'Sales' },
      { path: 'sales/new', ...needs('sales.create'), loadComponent: () => import('./features/sales/sale-new').then(m => m.SaleNew), title: 'New sale' },
      { path: 'sales/:id', ...needs('sales.view'), loadComponent: () => import('./features/sales/sale-detail').then(m => m.SaleDetail), title: 'Sale' },
      { path: 'sales/:id/edit', ...needs('sales.edit', { mode: 'edit' }), loadComponent: () => import('./features/sales/sale-new').then(m => m.SaleNew), title: 'Edit sale' },
      { path: 'stock', ...needs('stock.view'), loadComponent: () => import('./features/stock/stock').then(m => m.StockPage), title: 'Stock' },
      { path: 'products', ...needs('products.view'), loadComponent: () => import('./features/products/products').then(m => m.ProductsPage), title: 'Products' },
      { path: 'customers', ...needs('customers.view'), loadComponent: () => import('./features/customers/customers').then(m => m.CustomersPage), title: 'Customers' },
      { path: 'partners', ...needs('partners.manage'), loadComponent: () => import('./features/partners/partners').then(m => m.PartnersPage), title: 'Trade partners' },
      { path: 'debtors', ...needs('customers.view'), loadComponent: () => import('./features/debtors/debtors').then(m => m.DebtorsPage), title: 'Debtors' },
      { path: 'suppliers', ...needs('suppliers.manage'), loadComponent: () => import('./features/suppliers/suppliers').then(m => m.SuppliersPage), title: 'Suppliers' },
      { path: 'suppliers/:id', ...needs('suppliers.manage'), loadComponent: () => import('./features/suppliers/supplier-detail').then(m => m.SupplierDetail), title: 'Supplier' },
      // Buying from a supplier means picking from THEIR OWN item list; it records what was bought and owed and never moves stock.
      { path: 'purchases', ...needs('purchases.view'), loadComponent: () => import('./features/supplies/supplies').then(m => m.SuppliesPage), title: 'Purchases' },
      { path: 'supplies', redirectTo: 'purchases' },
      // History only: purchase orders raised the old way stay readable (and printable) so nothing is stranded. There is no longer any way to
      // create or edit one — buying from a supplier happens on /purchases, from that supplier's own item list.
      { path: 'stock-purchases', ...needs('purchases.view'), loadComponent: () => import('./features/purchases/purchases-list').then(m => m.PurchasesList), title: 'Older stock purchases' },
      { path: 'stock-purchases/:id', ...needs('purchases.view'), loadComponent: () => import('./features/purchases/purchase-detail').then(m => m.PurchaseDetail), title: 'Stock purchase' },
      { path: 'finance', ...needs('finance.view'), loadComponent: () => import('./features/finance/finance').then(m => m.FinancePage), title: 'Finance' },
      { path: 'users', ...needs('users.manage'), loadComponent: () => import('./features/users/users').then(m => m.UsersPage), title: 'People & access' },
      { path: 'quotations', ...needs('sales.view'), loadComponent: () => import('./features/quotations/quotations').then(m => m.QuotationsPage), title: 'Quotations' },
      { path: 'quotations/new', ...needs('quotations.create', { mode: 'quote' }), loadComponent: () => import('./features/sales/sale-new').then(m => m.SaleNew), title: 'New quotation' },
      { path: 'waybills', ...needs('waybills.manage'), loadComponent: () => import('./features/quotations/waybills').then(m => m.WaybillsPage), title: 'Waybills' },
      { path: 'serials', ...needs('serials.manage'), loadComponent: () => import('./features/serials/serials').then(m => m.SerialsPage), title: 'Serial numbers' },
      { path: 'price-book', ...needs('pricebook.view'), loadComponent: () => import('./features/pricebook/pricebook').then(m => m.PricebookPage), title: 'Price book' },
      { path: 'team', ...needs('attendance.view'), loadComponent: () => import('./features/team/team').then(m => m.TeamPage), title: 'Team' },
      { path: 'expenses', ...needs('expenses.manage'), loadComponent: () => import('./features/money/expenses').then(m => m.ExpensesPage), title: 'Expenses' },
      { path: 'rebates', ...needs('rebates.manage'), loadComponent: () => import('./features/money/rebates').then(m => m.RebatesPage), title: 'Rebates' },
      { path: 'company', ...needs('company.edit'), loadComponent: () => import('./features/company/company').then(m => m.CompanyPage), title: 'Company & documents' },
      { path: 'website', canActivate: [permGuard, marketingGuard], data: { perm: 'sitecontent.manage' }, loadComponent: () => import('./features/website/website').then(m => m.WebsitePage), title: 'Website' },
      { path: 'campaigns', canActivate: [permGuard, marketingGuard], data: { perm: 'campaigns.manage' }, loadComponent: () => import('./features/campaigns/campaigns').then(m => m.CampaignsPage), title: 'Campaigns' },
      { path: 'marketing-approvals', canActivate: [permGuard, marketingGuard], data: { perm: 'marketing.approve' }, loadComponent: () => import('./features/campaigns/marketing-approvals').then(m => m.MarketingApprovalsPage), title: 'Marketing approvals' },
      { path: 'analytics', ...needs('analytics.view'), loadComponent: () => import('./features/analytics/analytics').then(m => m.AnalyticsPage), title: 'Analytics' },
      { path: 'storage', ...needs('storage.manage'), loadComponent: () => import('./features/system/storage').then(m => m.StoragePage), title: 'Database storage' },
      // The record-keeping apps: Spreadsheet and Time Off have screens of their own; every other app uses the shared records screen (its definition comes from the server).
      { path: 'apps/spreadsheet', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'spreadsheet.manage' }, loadComponent: () => import('./features/apps-engine/spreadsheet').then(m => m.SpreadsheetPage), title: 'Spreadsheet' },
      { path: 'apps/:app', canActivate: [appPermGuard, appGuard], loadComponent: () => import('./features/apps-engine/engine').then(m => m.EnginePage), title: 'App' },
      { path: 'accounting', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'gl.view' }, loadComponent: () => import('./features/accounting/accounting').then(m => m.AccountingPage), title: 'Accounting' },
      { path: 'tax', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'tax.manage' }, loadComponent: () => import('./features/accounting/tax').then(m => m.TaxPage), title: 'Tax' },
      { path: 'banking', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'bank.reconcile' }, loadComponent: () => import('./features/accounting/banking').then(m => m.BankingPage), title: 'Bank reconciliation' },
      { path: 'approvals', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'approvals.decide|company.edit' }, loadComponent: () => import('./features/accounting/approvals').then(m => m.ApprovalsPage), title: 'Approvals' },
      { path: 'budgets', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'gl.view' }, loadComponent: () => import('./features/erp/budgets').then(m => m.BudgetsPage), title: 'Budgets' },
      { path: 'fixed-assets', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'gl.view' }, loadComponent: () => import('./features/erp/fixed-assets').then(m => m.FixedAssetsPage), title: 'Fixed assets' },
      { path: 'currencies', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'gl.view' }, loadComponent: () => import('./features/erp/currencies').then(m => m.CurrenciesPage), title: 'Currencies' },
      { path: 'branches', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'company.edit' }, loadComponent: () => import('./features/branches/branches').then(m => m.BranchesPage), title: 'Branches' },
      { path: 'time-off', canActivate: [permGuard, newFeaturesGuard], data: { perm: 'timeoff.manage' }, loadComponent: () => import('./features/apps/time-off').then(m => m.TimeOffPage), title: 'Time Off' },
      { path: 'appearance', loadComponent: () => import('./features/system/appearance').then(m => m.AppearancePage), title: 'Appearance' },
      { path: 'activity', ...needs('audit.view'), loadComponent: () => import('./features/users/activity').then(m => m.ActivityPage), title: 'Activity log' },
    ],
  },
  { path: '**', redirectTo: '' },
];
