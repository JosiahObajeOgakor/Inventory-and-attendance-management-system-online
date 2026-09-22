import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/http';
import { Shell } from './layout/shell';

// Every feature is lazy-loaded. The guards only shape the UI: the API enforces the same roles on every endpoint.
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
      { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard').then(m => m.DashboardPage), title: 'Dashboard' },
      { path: 'sales', loadComponent: () => import('./features/sales/sales-list').then(m => m.SalesList), title: 'Sales' },
      { path: 'sales/new', loadComponent: () => import('./features/sales/sale-new').then(m => m.SaleNew), title: 'New sale' },
      { path: 'sales/:id', loadComponent: () => import('./features/sales/sale-detail').then(m => m.SaleDetail), title: 'Sale' },
      { path: 'stock', loadComponent: () => import('./features/stock/stock').then(m => m.StockPage), title: 'Stock' },
      { path: 'products', loadComponent: () => import('./features/products/products').then(m => m.ProductsPage), title: 'Products' },
      { path: 'customers', canActivate: [adminGuard], loadComponent: () => import('./features/customers/customers').then(m => m.CustomersPage), title: 'Customers' },
      { path: 'suppliers', canActivate: [adminGuard], loadComponent: () => import('./features/suppliers/suppliers').then(m => m.SuppliersPage), title: 'Suppliers' },
      { path: 'purchases', canActivate: [adminGuard], loadComponent: () => import('./features/purchases/purchases-list').then(m => m.PurchasesList), title: 'Purchases' },
      { path: 'purchases/new', canActivate: [adminGuard], loadComponent: () => import('./features/purchases/purchase-new').then(m => m.PurchaseNew), title: 'New purchase' },
      { path: 'purchases/:id', canActivate: [adminGuard], loadComponent: () => import('./features/purchases/purchase-detail').then(m => m.PurchaseDetail), title: 'Purchase' },
      { path: 'finance', canActivate: [adminGuard], loadComponent: () => import('./features/finance/finance').then(m => m.FinancePage), title: 'Finance' },
      { path: 'users', canActivate: [adminGuard], loadComponent: () => import('./features/users/users').then(m => m.UsersPage), title: 'People & access' },
      { path: 'quotations', loadComponent: () => import('./features/quotations/quotations').then(m => m.QuotationsPage), title: 'Quotations' },
      { path: 'quotations/new', loadComponent: () => import('./features/sales/sale-new').then(m => m.SaleNew), data: { mode: 'quote' }, title: 'New quotation' },
      { path: 'waybills', loadComponent: () => import('./features/quotations/waybills').then(m => m.WaybillsPage), title: 'Waybills' },
      { path: 'serials', loadComponent: () => import('./features/serials/serials').then(m => m.SerialsPage), title: 'Serial numbers' },
      { path: 'price-book', loadComponent: () => import('./features/pricebook/pricebook').then(m => m.PricebookPage), title: 'Price book' },
      { path: 'team', canActivate: [adminGuard], loadComponent: () => import('./features/team/team').then(m => m.TeamPage), title: 'Team' },
      { path: 'expenses', canActivate: [adminGuard], loadComponent: () => import('./features/money/expenses').then(m => m.ExpensesPage), title: 'Expenses' },
      { path: 'rebates', canActivate: [adminGuard], loadComponent: () => import('./features/money/rebates').then(m => m.RebatesPage), title: 'Rebates' },
      { path: 'company', canActivate: [adminGuard], loadComponent: () => import('./features/company/company').then(m => m.CompanyPage), title: 'Company & documents' },
      { path: 'analytics', canActivate: [adminGuard], loadComponent: () => import('./features/analytics/analytics').then(m => m.AnalyticsPage), title: 'Analytics' },
      { path: 'storage', canActivate: [adminGuard], loadComponent: () => import('./features/system/storage').then(m => m.StoragePage), title: 'Database storage' },
      { path: 'appearance', loadComponent: () => import('./features/system/appearance').then(m => m.AppearancePage), title: 'Appearance' },
      { path: 'activity', canActivate: [adminGuard], loadComponent: () => import('./features/users/activity').then(m => m.ActivityPage), title: 'Activity log' },
    ],
  },
  { path: '**', redirectTo: '' },
];
