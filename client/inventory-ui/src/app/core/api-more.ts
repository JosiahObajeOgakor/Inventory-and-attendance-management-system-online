import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Paged, PageQuery, SaleResult } from './models';
import * as N from './models-more';
import * as D from './models-dash';

function params(q?: object): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(q ?? {})) if (v !== undefined && v !== null && v !== '') p = p.set(k, String(v));
  return p;
}

/** Endpoints for the documents, staff and finance features (kept apart from the core Api so each file stays readable). */
@Injectable({ providedIn: 'root' })
export class Api2 {
  private readonly http = inject(HttpClient);
  private get<T>(url: string, q?: object) { return firstValueFrom(this.http.get<T>(url, { params: params(q) })); }
  private post<T>(url: string, body: unknown = {}) { return firstValueFrom(this.http.post<T>(url, body)); }
  private put<T>(url: string, body: unknown) { return firstValueFrom(this.http.put<T>(url, body)); }
  private del<T>(url: string) { return firstValueFrom(this.http.delete<T>(url)); }

  // company & documents
  companyProfile() { return this.get<N.CompanyProfile>('/api/company/profile'); }
  saveCompanyProfile(p: Omit<N.CompanyProfile, 'assets' | 'hasPriceLists' | 'buysGoods'>) { return this.put<void>('/api/company/profile', p); }
  uploadAsset(kind: N.AssetKind, file: File) { const f = new FormData(); f.append('file', file); return firstValueFrom(this.http.put<void>(`/api/company/assets/${kind}`, f)); }
  removeAsset(kind: N.AssetKind) { return this.del<void>(`/api/company/assets/${kind}`); }

  // quotations & waybills
  quotations(q: PageQuery) { return this.get<Paged<N.QuotationRow>>('/api/quotations', q); }
  quotation(id: number) { return this.get<N.QuotationDetail>(`/api/quotations/${id}`); }
  createQuotation(r: N.QuoteRequest) { return this.post<N.QuoteResult>('/api/quotations', r); }
  convertQuotation(id: number, r: { warehouseId: number; paymentMethod: string; paidNow: number; dueDate: string | null }) { return this.post<SaleResult>(`/api/quotations/${id}/convert`, r); }
  deleteQuotation(id: number) { return this.del<void>(`/api/quotations/${id}`); }
  waybills(q: PageQuery) { return this.get<Paged<N.WaybillRow>>('/api/waybills', q); }
  waybillInvoices(q: PageQuery & { pendingOnly?: boolean }) { return this.get<Paged<N.PendingInvoice>>('/api/waybills/invoices', q); }
  waybillPrefill(invoiceId: number) { return this.get<N.WaybillPrefill>(`/api/waybills/prefill/${invoiceId}`); }
  createWaybill(r: N.WaybillInput) { return this.post<{ id: number }>('/api/waybills', r); }

  // attendance
  attendanceToday() { return this.get<N.AttendanceToday>('/api/attendance/today'); }
  checkIn() { return this.post<{ at: string }>('/api/attendance/check-in'); }
  declineCheckIn() { return this.post<void>('/api/attendance/decline'); }
  attendanceEvents(q: PageQuery & { from: string; to: string }) { return this.get<Paged<N.AttendanceEvent>>('/api/attendance/events', q); }
  attendanceDaily(from: string, to: string) { return this.get<N.AttendanceDay[]>('/api/attendance/daily', { from, to }); }

  // employees & payroll
  employees() { return this.get<N.Employee[]>('/api/employees'); }
  createEmployee(i: N.EmployeeInput) { return this.post<{ id: number }>('/api/employees', i); }
  updateEmployee(id: number, i: N.EmployeeInput) { return this.put<void>(`/api/employees/${id}`, i); }
  setEmployeeActive(id: number, active: boolean) { return this.put<void>(`/api/employees/${id}/active`, { active }); }
  deleteEmployee(id: number) { return this.del<void>(`/api/employees/${id}`); }
  employeeHistory(id: number) { return this.get<N.PayrollRow[]>(`/api/employees/${id}/payroll`); }
  employeeLoans(id: number) { return this.get<N.Loan[]>(`/api/employees/${id}/loans`); }
  addLoan(id: number, amount: number, note: string | null) { return this.post<{ id: number }>(`/api/employees/${id}/loans`, { amount, note }); }
  payrollMonth(y: number, m: number) { return this.get<N.PayrollRow[]>(`/api/payroll/${y}/${m}`); }
  generatePayroll(year: number, month: number) { return this.post<{ added: number }>('/api/payroll/generate', { year, month }); }
  editPayrollRow(id: number, r: { salaryAmount: number; loanDeduction: number; note: string | null }) { return this.put<void>(`/api/payroll/rows/${id}`, r); }
  deletePayrollRow(id: number) { return this.del<void>(`/api/payroll/rows/${id}`); }
  payRow(id: number) { return this.post<{ fullName: string; period: string; netPay: number; alreadyPaid: boolean }>(`/api/payroll/rows/${id}/pay`); }
  payAll(y: number, m: number) { return this.post<{ paid: number }>(`/api/payroll/${y}/${m}/pay-all`); }
  repayLoan(loanId: number, amount: number, note: string | null) { return this.post<void>(`/api/payroll/loans/${loanId}/repay`, { amount, note }); }
  deleteLoan(loanId: number) { return this.del<void>(`/api/payroll/loans/${loanId}`); }

  // serial numbers
  serials(productId: number, q: { status?: string; warehouseId?: number; search?: string }) { return this.get<N.Serial[]>(`/api/serials/product/${productId}`, q); }
  receiveSerials(productId: number, warehouseId: number, serials: string, notes: string | null) { return this.post<{ received: number }>(`/api/serials/product/${productId}/receive`, { warehouseId, serials, notes }); }
  takeBackSerial(productId: number, serial: string, warehouseId: number, reason: string | null) { return this.post<void>(`/api/serials/product/${productId}/take-back`, { serial, warehouseId, reason }); }
  writeOffSerial(productId: number, serial: string, reason: string) { return this.post<void>(`/api/serials/product/${productId}/write-off`, { serial, reason }); }
  serialDiscrepancies() { return this.get<N.SerialDiscrepancy[]>('/api/serials/discrepancies'); }

  // expenses & rebates
  expenseCategories() { return this.get<string[]>('/api/expenses/categories'); }
  expenses(q: PageQuery & { from: string; to: string; category?: string }) { return this.get<N.ExpenseList>('/api/expenses', q); }
  createExpense(i: N.ExpenseInput) { return this.post<{ id: number }>('/api/expenses', i); }
  updateExpense(id: number, i: N.ExpenseInput) { return this.put<void>(`/api/expenses/${id}`, i); }
  deleteExpense(id: number) { return this.del<void>(`/api/expenses/${id}`); }
  rebates(search?: string) { return this.get<N.RebateSummary>('/api/rebates', { search }); }
  rebateEntries(customerId: number) { return this.get<N.RebateEntry[]>(`/api/rebates/customer/${customerId}`); }
  redeemRebate(customerId: number) { return this.post<{ customer: string; amount: number }>(`/api/rebates/customer/${customerId}/redeem`); }
  setRebateRate(pct: number) { return this.put<void>('/api/rebates/default-rate', { pct }); }

  // price book (Candid) & metrics
  priceBook(q: PageQuery & { categoryId?: number }) { return this.get<N.PriceBook>('/api/price-book', q); }
  priceHistory(q: PageQuery & { productId?: number }) { return this.get<Paged<N.PriceHistory>>('/api/price-book/history', q); }
  applyPrices(updates: { productId: number; distributor: number; wholesaler: number; retail: number }[], note: string | null) { return this.post<{ changed: number }>('/api/price-book/apply', { updates, note }); }
  addPriceProducts(products: { name: string; categoryId: number; unit: string | null; distributor: number; wholesaler: number; retail: number }[], note: string | null) { return this.post<{ ids: number[] }>('/api/price-book/products', { products, note }); }
  customerMetrics(id: number) { return this.get<N.CustomerMetrics>(`/api/customers/${id}/metrics`); }

  // admin dashboard, assistant, email
  overview(days = 30) { return this.get<D.Overview>('/api/dashboard/overview', { days }); }
  calendar(year: number, month: number) { return this.get<D.CalendarData>('/api/dashboard/calendar', { year, month }); }
  insights(refresh = false) { return this.get<D.InsightSet>('/api/ai/insights', { refresh }); }
  aiStatus() { return this.get<D.AiStatus>('/api/ai/status'); }
  aiAsk(question: string, history: D.ChatTurn[]) { return this.post<D.AssistantAnswer>('/api/ai/ask', { question, history }); }
  forecast() { return this.get<D.ForecastRow[]>('/api/analytics/forecast'); }
  soldTogether() { return this.get<D.BasketRule[]>('/api/analytics/sold-together'); }
  unusualMovements() { return this.get<D.UnusualRow[]>('/api/analytics/unusual'); }
  suggestedOrder(supplierId: number) { return this.get<D.SuggestedLine[]>(`/api/analytics/suggested-order/${supplierId}`); }
  saleAdvice(customerId: number | null, lines: { productId: number; quantity: number }[]) { return this.post<D.AdviceLine[]>('/api/analytics/sale-advice', { customerId, lines }); }
  purchaseAdvice(supplierId: number, lines: { productId: number; unitCost: number }[]) { return this.post<D.AdviceLine[]>('/api/analytics/purchase-advice', { supplierId, lines }); }
  emailStatus() { return this.get<{ configured: boolean }>('/api/email/status'); }
  previewQuotationEmail(id: number, to: string | null, note: string | null) { return this.post<D.EmailPreview>(`/api/email/quotations/${id}/preview`, { to, note }); }
  sendQuotationEmail(id: number, to: string | null, note: string | null) { return this.post<D.EmailSent>(`/api/email/quotations/${id}/send`, { to, note }); }
  previewPriceListEmail(customerId: number | null, tier: string | null, to: string | null, note: string | null) { return this.post<D.EmailPreview>('/api/email/price-list/preview', { customerId, tier, to, note }); }
  sendPriceListEmail(customerId: number | null, tier: string | null, to: string | null, note: string | null) { return this.post<D.EmailSent>('/api/email/price-list/send', { customerId, tier, to, note }); }
}
