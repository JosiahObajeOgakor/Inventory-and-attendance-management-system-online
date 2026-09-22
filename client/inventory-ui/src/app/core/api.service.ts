import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import * as M from './models';

function params(q?: object): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(q ?? {})) if (v !== undefined && v !== null && v !== '') p = p.set(k, String(v));
  return p;
}

/** A readable message from any failed request. Never shows stack traces or raw server text. */
export function messageOf(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const p = err.error as M.Problem | null;
    if (err.status === 0) return 'Can’t reach the server. Check your connection and try again.';
    if (p?.errors) return Object.values(p.errors).flat().join(' ');
    if (p?.detail) return p.detail;
    if (p?.title) return p.title;
    if (err.status === 429) return 'Too many attempts. Wait a minute and try again.';
    if (err.status === 403) return 'Your account isn’t allowed to do that.';
  }
  return 'Something went wrong. Please try again.';
}

export function problemOf(err: unknown): M.Problem | null {
  return err instanceof HttpErrorResponse ? (err.error as M.Problem | null) : null;
}

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  private get<T>(url: string, q?: object) { return firstValueFrom(this.http.get<T>(url, { params: params(q) })); }
  private post<T>(url: string, body: unknown = {}, headers?: Record<string, string>) { return firstValueFrom(this.http.post<T>(url, body, { headers })); }
  private put<T>(url: string, body: unknown) { return firstValueFrom(this.http.put<T>(url, body)); }
  private del<T>(url: string) { return firstValueFrom(this.http.delete<T>(url)); }

  // auth
  login(username: string, password: string, company: string) { return this.post<M.Me>('/api/auth/login', { username, password, company }); }
  logout() { return this.post<void>('/api/auth/logout'); }
  me() { return this.get<M.Me>('/api/auth/me'); }
  companies() { return this.get<M.CompanyChoice[]>('/api/auth/companies'); }
  changePassword(currentPassword: string, newPassword: string) { return this.post<void>('/api/auth/change-password', { currentPassword, newPassword }); }
  switchCompany(company: string) { return this.post<M.Me>('/api/auth/company', { company }); }

  // dashboard & finance
  dashboard() { return this.get<M.Dashboard>('/api/dashboard'); }
  financeSummary(year?: number, month?: number) { return this.get<M.FinanceSummary>('/api/finance/summary', { year, month }); }
  income(year: number) { return this.get<M.MonthlyIncome[]>('/api/finance/income', { year }); }
  ledger(q: M.PageQuery & { accountType?: string }) { return this.get<M.Paged<M.LedgerRow>>('/api/finance/ledger', q); }

  // catalogue & stock
  products(q: M.PageQuery & { includeInactive?: boolean }) { return this.get<M.Paged<M.Product>>('/api/products', q); }
  product(id: number) { return this.get<M.Product>(`/api/products/${id}`); }
  productByBarcode(code: string) { return this.get<M.Product>(`/api/products/by-barcode/${encodeURIComponent(code)}`); }
  createProduct(req: M.NewProductRequest) { return this.post<{ id: number }>('/api/products', req); }
  updateProduct(id: number, input: M.ProductInput) { return this.put<void>(`/api/products/${id}`, input); }
  deleteProduct(id: number) { return this.del<{ deleted: boolean; deactivated: boolean }>(`/api/products/${id}`); }
  categories() { return this.get<M.Category[]>('/api/categories'); }
  warehouses() { return this.get<M.Warehouse[]>('/api/warehouses'); }
  addCategory(name: string) { return this.post<{ id: number }>('/api/categories', { name }); }
  addWarehouse(name: string, location: string | null) { return this.post<{ id: number }>('/api/warehouses', { name, location }); }
  batches(q: M.PageQuery & { onlyLow?: boolean }) { return this.get<M.Paged<M.BatchRow>>('/api/inventory/batches', q); }
  movements(q: M.PageQuery & { referenceType?: string; productId?: number }) { return this.get<M.Paged<M.MovementRow>>('/api/inventory/movements', q); }
  produce(r: { productId: number; warehouseId: number; quantity: number; producedOn?: string | null; batchNumber?: string | null; expiryDate?: string | null }) { return this.post<{ movementId: number }>('/api/stock/production', r); }
  transfer(r: { productId: number; fromWarehouseId: number; toWarehouseId: number; quantity: number }) { return this.post<void>('/api/stock/transfers', r); }
  adjust(r: { batchId: number; delta: number; reason: string }) { return this.post<void>('/api/stock/adjustments', r); }

  // sales
  sales(q: M.PageQuery) { return this.get<M.Paged<M.InvoiceRow>>('/api/sales', q); }
  sale(id: number) { return this.get<M.InvoiceDetail>(`/api/sales/${id}`); }
  saleByNumber(code: string) { return this.get<M.InvoiceDetail>(`/api/sales/by-number/${encodeURIComponent(code)}`); }
  previewSale(req: M.SaleRequest) { return this.post<M.SalePreview>('/api/sales/preview', req); }
  createSale(req: M.SaleRequest, idempotencyKey: string) { return this.post<M.SaleResult>('/api/sales', req, { 'Idempotency-Key': idempotencyKey }); }
  voidSale(id: number, reason: string) { return this.post<void>(`/api/sales/${id}/void`, { reason }); }

  // customers
  customers(q: M.PageQuery) { return this.get<M.Paged<M.Customer>>('/api/customers', q); }
  customerLookup(search?: string) { return this.get<M.CustomerLookup[]>('/api/customers/lookup', { search }); }
  createCustomer(input: M.CustomerInput) { return this.post<{ id: number }>('/api/customers', input); }
  updateCustomer(id: number, input: M.CustomerInput) { return this.put<void>(`/api/customers/${id}`, input); }
  deleteCustomer(id: number) { return this.del<void>(`/api/customers/${id}`); }
  recordPayment(id: number, amount: number, method: string) { return this.post<{ applied: number }>(`/api/customers/${id}/payments`, { amount, method }); }

  // suppliers & purchases
  suppliers(q: M.PageQuery) { return this.get<M.Paged<M.Supplier>>('/api/suppliers', q); }
  createSupplier(input: M.SupplierInput) { return this.post<{ id: number }>('/api/suppliers', input); }
  updateSupplier(id: number, input: M.SupplierInput) { return this.put<void>(`/api/suppliers/${id}`, input); }
  deleteSupplier(id: number) { return this.del<void>(`/api/suppliers/${id}`); }
  purchases(q: M.PageQuery) { return this.get<M.Paged<M.PurchaseRow>>('/api/purchases', q); }
  purchase(id: number) { return this.get<M.PurchaseDetail>(`/api/purchases/${id}`); }
  createPurchase(req: M.PurchaseRequest, idempotencyKey: string) { return this.post<M.PurchaseResult>('/api/purchases', req, { 'Idempotency-Key': idempotencyKey }); }
  receivePurchase(id: number, warehouseId: number) { return this.post<void>(`/api/purchases/${id}/receive`, { warehouseId }); }
  payPurchase(id: number) { return this.post<void>(`/api/purchases/${id}/pay`); }

  // admin
  users() { return this.get<M.UserRow[]>('/api/users'); }
  createUser(r: { fullName: string; username: string; password: string; role: string; companies: string[] }) { return this.post<{ id: number }>('/api/users', r); }
  updateUser(id: number, r: { fullName: string; role: string; companies: string[]; isActive: boolean }) { return this.put<void>(`/api/users/${id}`, r); }
  resetPassword(id: number, newPassword: string) { return this.post<void>(`/api/users/${id}/reset-password`, { newPassword }); }
  audit(q: M.PageQuery) { return this.get<M.Paged<M.AuditRow>>('/api/audit', q); }
  loginAudit(q: M.PageQuery) { return this.get<M.Paged<M.LoginAuditRow>>('/api/audit/logins', q); }
}
