import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface Branch { id: number; code: string; name: string; address: string | null; state: string | null; phone: string | null; isHeadOffice: boolean; active: boolean;
  warehouses: { id: number; name: string }[]; }
export interface BranchPerformance { branchId: number; branch: string; income: number; expenses: number; netProfit: number; stockValue: number; }
export interface BranchUser { userId: number; username: string; fullName: string; branchId: number | null; }

export interface Asset { id: number; code: string; name: string; category: string; serialNumber: string | null; location: string | null; branchId: number | null; acquiredOn: string;
  cost: number; residualValue: number; method: string; usefulLifeMonths: number; ratePct: number; accumulatedDepreciation: number; bookValue: number; monthlyCharge: number;
  status: string; disposedOn: string | null; disposalProceeds: number | null; disposalGainLoss: number | null; }
export interface AssetRegister { assets: Asset[]; totalCost: number; totalAccumulated: number; totalBookValue: number;
  runs: { id: number; year: number; month: number; total: number; assets: number; runAt: string }[];
  byCategory: { category: string; count: number; cost: number; bookValue: number }[]; categories: string[]; }
export interface DepPreview { year: number; month: number; alreadyRun: boolean; total: number; lines: { assetId: number; code: string; name: string; amount: number }[]; }

export interface BudgetSummary { id: number; name: string; year: number; branchId: number | null; status: string; income: number; expenses: number; profit: number; approvedBy: string | null; }
export interface BudgetRow { code: string; account: string; type: string; months: number[]; total: number; }
export interface BudgetDetail { summary: BudgetSummary; rows: BudgetRow[]; }
export interface VarianceRow { code: string; account: string; type: string; budget: number; actual: number; variance: number; variancePct: number | null; favourable: boolean; }
export interface Variance { budgetId: number; name: string; year: number; toMonth: number; rows: VarianceRow[]; budgetProfit: number; actualProfit: number; }

export interface CurrencyRow { code: string; name: string; symbol: string; active: boolean; latestRate: number | null; latestDate: string | null; }
export interface FxRate { id: number; currency: string; date: string; rate: number; source: string | null; }
export interface FxDoc { id: number; number: string; kind: string; party: string; currency: string; date: string; dueDate: string | null; description: string; foreignAmount: number;
  rate: number; baseAmount: number; foreignOutstanding: number; settled: boolean; settlements: { date: string; foreignAmount: number; rate: number; baseAmount: number; gainLoss: number }[]; }
export interface Revaluation { asOf: string; rows: { number: string; kind: string; party: string; currency: string; foreignOutstanding: number; bookRate: number; closingRate: number; unrealised: number }[];
  totalUnrealised: number; posted: boolean; }

export interface GroupLine { code: string; name: string; type: string; byCompany: Record<string, number>; eliminations: number; group: number; }
export interface GroupStatement { from: string; to: string; companies: string[]; lines: GroupLine[]; groupIncome: number; groupExpenses: number; groupProfit: number;
  groupAssets: number; groupLiabilities: number; groupEquity: number; balanced: boolean; }
export interface Elimination { id: number; asOf: string; accountCode: string; debit: number; credit: number; note: string; createdBy: string; }

export interface EInvoice { id: number; invoiceId: number; invoiceNumber: string; invoiceDate: string; customer: string; total: number; irn: string; status: string; lastError: string | null;
  attempts: number; updatedAt: string; transmittedAt: string | null; hasQr: boolean; }
export interface EInvoiceStatus { configured: boolean; serviceId: string | null; ready: number; transmitted: number; failed: number; recent: EInvoice[]; }

/** Full ERP modules: branches, fixed assets, budgets, currencies, group accounts, e-invoicing. */
@Injectable({ providedIn: 'root' })
export class Erp2Api {
  private readonly http = inject(HttpClient);
  private get<T>(u: string, p?: Record<string, string | number | boolean | undefined | null>) {
    const clean = Object.fromEntries(Object.entries(p ?? {}).filter(([, v]) => v !== undefined && v !== null && v !== '')) as Record<string, string | number | boolean>;
    return firstValueFrom(this.http.get<T>(u, { params: new HttpParams({ fromObject: clean }) }));
  }
  private post<T>(u: string, body: unknown = {}) { return firstValueFrom(this.http.post<T>(u, body)); }
  private put(u: string, body: unknown) { return firstValueFrom(this.http.put<void>(u, body)); }
  private del(u: string) { return firstValueFrom(this.http.delete<void>(u)); }

  branches() { return this.get<Branch[]>('api/branches'); }
  saveBranch(id: number | null, b: { code: string; name: string; address: string | null; state: string | null; phone: string | null; active: boolean }) {
    return id ? this.put(`api/branches/${id}`, b) : this.post('api/branches', b);
  }
  assignWarehouse(warehouseId: number, branchId: number) { return this.put(`api/branches/warehouses/${warehouseId}`, { branchId }); }
  branchPerformance(from?: string, to?: string) { return this.get<BranchPerformance[]>('api/branches/performance', { from, to }); }
  branchUsers() { return this.get<BranchUser[]>('api/branches/users'); }
  assignUser(userId: number, branchId: number | null) { return this.put(`api/branches/users/${userId}`, { branchId }); }

  assets() { return this.get<AssetRegister>('api/assets'); }
  addAsset(a: unknown) { return this.post<{ id: number }>('api/assets', a); }
  depreciationPreview(year: number, month: number) { return this.get<DepPreview>(`api/assets/depreciation/${year}/${month}`); }
  runDepreciation(year: number, month: number) { return this.post<{ id: number }>('api/assets/depreciation', { year, month }); }
  disposeAsset(id: number, d: { disposedOn: string; proceeds: number; receivedInto: string; note: string | null }) { return this.post(`api/assets/${id}/dispose`, d); }

  budgets() { return this.get<BudgetSummary[]>('api/budgets'); }
  budget(id: number) { return this.get<BudgetDetail>(`api/budgets/${id}`); }
  createBudget(b: { name: string; year: number; branchId: number | null; start: string; growthPct: number }) { return this.post<{ id: number }>('api/budgets', b); }
  saveBudgetRows(id: number, rows: { code: string; months: number[] }[]) { return this.put(`api/budgets/${id}/rows`, rows); }
  approveBudget(id: number, approve: boolean) { return this.post(`api/budgets/${id}/${approve ? 'approve' : 'reopen'}`); }
  deleteBudget(id: number) { return this.del(`api/budgets/${id}`); }
  variance(id: number, toMonth: number) { return this.get<Variance>(`api/budgets/${id}/variance`, { toMonth }); }

  currencies() { return this.get<CurrencyRow[]>('api/currencies'); }
  addCurrency(code: string) { return this.post('api/currencies', { code }); }
  rates(code: string) { return this.get<FxRate[]>(`api/currencies/${code}/rates`); }
  setRate(code: string, date: string, rate: number, source: string | null) { return this.put(`api/currencies/${code}/rates`, { date, rate, source }); }
  fxDocuments(openOnly: boolean) { return this.get<FxDoc[]>('api/currencies/documents', { openOnly }); }
  bookFx(d: unknown) { return this.post<{ id: number }>('api/currencies/documents', d); }
  settleFx(id: number, s: { date: string; foreignAmount: number; rate: number | null; bankCode: string | null }) { return this.post(`api/currencies/documents/${id}/settle`, s); }
  revaluation(asOf: string) { return this.get<Revaluation>('api/currencies/revaluation', { asOf }); }
  postRevaluation(asOf: string) { return this.post('api/currencies/revaluation', { asOf }); }

  group(from?: string, to?: string) { return this.get<GroupStatement>('api/consolidation', { from, to }); }
  eliminations() { return this.get<Elimination[]>('api/consolidation/eliminations'); }
  addElimination(e: { asOf: string; accountCode: string; debit: number; credit: number; note: string }) { return this.post('api/consolidation/eliminations', e); }
  deleteElimination(id: number) { return this.del(`api/consolidation/eliminations/${id}`); }

  einvoicing() { return this.get<EInvoiceStatus>('api/einvoicing'); }
  einvoicePayload(invoiceId: number) { return firstValueFrom(this.http.get(`api/einvoicing/invoices/${invoiceId}/payload`, { responseType: 'text' })); }
  submitEInvoice(invoiceId: number) { return this.post<{ status: string }>(`api/einvoicing/invoices/${invoiceId}/submit`); }
  retryEInvoice(invoiceId: number) { return this.post<{ status: string }>(`api/einvoicing/invoices/${invoiceId}/retry`); }
}
