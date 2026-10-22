import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface Account { id: number; code: string; name: string; type: string; isControl: boolean; isActive: boolean; }
export interface BalanceRow { code: string; name: string; type: string; debit: number; credit: number; balance: number; }
export interface TrialBalance { asOf: string; rows: BalanceRow[]; totalDebit: number; totalCredit: number; balanced: boolean; }
export interface StatementLine { code: string; name: string; amount: number; }
export interface ProfitAndLoss { from: string; to: string; income: StatementLine[]; totalIncome: number; expenses: StatementLine[]; totalExpenses: number; netProfit: number; }
export interface BalanceSheet { asOf: string; assets: StatementLine[]; totalAssets: number; liabilities: StatementLine[]; totalLiabilities: number; equity: StatementLine[];
  earningsToDate: number; totalEquity: number; balanced: boolean; }
export interface LedgerLine { journalId: number; date: string; memo: string; source: string; debit: number; credit: number; runningBalance: number; }
export interface JournalLine { code: string; account: string; debit: number; credit: number; note: string | null; }
export interface Journal { id: number; date: string; source: string; memo: string; status: string; reversalOfId: number | null; preparedBy: string; approvedBy: string | null; lines: JournalLine[]; }
export interface Period { year: number; month: number; status: 'Open' | 'SoftClosed' | 'Locked'; closedAt: string | null; closedBy: string | null; note: string | null; }
export interface Approval { id: number; action: string; amount: number; summary: string; status: string; requestedBy: string; requestedAt: string; decidedBy: string | null;
  decidedAt: string | null; decisionNote: string | null; failureReason: string | null; }
export interface ApprovalRule { action: string; thresholdAmount: number; enabled: boolean; }
export interface Channel { id: number; key: string; name: string; model: 'managed' | 'merchant'; isActive: boolean; commissionPct: number; reservationMinutes: number; publishedListings: number; }
export interface Listing { listingId: number; productId: number; sku: string; name: string; unit: string; category: string; price: number; available: number; status: string; version: number; updatedAt: string; }
export interface PublishResult { productId: number; product: string; published: boolean; reason: string | null; }
export interface CreditNote { id: number; number: string; invoiceNumber: string; customer: string; noteDate: string; reason: string; restocked: boolean; total: number;
  appliedToInvoice: number; creditToCustomer: number; }

/** The ERP back office: general ledger, approvals inbox, sales channels and credit notes. */
@Injectable({ providedIn: 'root' })
export class ErpApi {
  private readonly http = inject(HttpClient);
  private get<T>(u: string, p?: Record<string, string | number | undefined | null>) {
    const clean = Object.fromEntries(Object.entries(p ?? {}).filter(([, v]) => v !== undefined && v !== null && v !== '')) as Record<string, string | number>;
    return firstValueFrom(this.http.get<T>(u, { params: new HttpParams({ fromObject: clean }) }));
  }
  private post<T>(u: string, body: unknown = {}) { return firstValueFrom(this.http.post<T>(u, body)); }
  private put(u: string, body: unknown) { return firstValueFrom(this.http.put<void>(u, body)); }

  // ---- general ledger
  accounts() { return this.get<Account[]>('api/gl/accounts'); }
  addAccount(code: string, name: string, type: string) { return this.post<{ id: number }>('api/gl/accounts', { code, name, type }); }
  trialBalance(asOf?: string) { return this.get<TrialBalance>('api/gl/trial-balance', { asOf }); }
  profitAndLoss(from?: string, to?: string) { return this.get<ProfitAndLoss>('api/gl/profit-and-loss', { from, to }); }
  balanceSheet(asOf?: string) { return this.get<BalanceSheet>('api/gl/balance-sheet', { asOf }); }
  ledger(code: string, from?: string, to?: string) { return this.get<LedgerLine[]>(`api/gl/accounts/${encodeURIComponent(code)}/ledger`, { from, to }); }
  journals(from?: string, to?: string, status?: string) { return this.get<Journal[]>('api/gl/journals', { from, to, status }); }
  draftJournal(date: string, memo: string, lines: { code: string; debit: number; credit: number; note: string | null }[]) { return this.post<{ id: number }>('api/gl/journals', { date, memo, lines }); }
  approveJournal(id: number) { return this.post(`api/gl/journals/${id}/approve`); }
  reverseJournal(id: number, reason: string) { return this.post(`api/gl/journals/${id}/reverse`, { reason }); }
  periods() { return this.get<Period[]>('api/gl/periods'); }
  setPeriod(year: number, month: number, status: string, note: string | null) { return this.put(`api/gl/periods/${year}/${month}`, { status, note }); }
  openingBalances(date: string, cashOnHand: number, bank: number) { return this.post<{ journalId: number }>('api/gl/opening-balances', { date, cashOnHand, bank }); }
  inventoryReconciliation() { return this.get<{ ledgerInventory: number; stockAtCost: number; difference: number }>('api/gl/inventory-reconciliation'); }

  // ---- approvals
  approvals(status = 'Pending') { return this.get<Approval[]>('api/approvals', { status }); }
  myApprovals() { return this.get<Approval[]>('api/approvals/mine'); }
  decide(id: number, approve: boolean, note: string | null) { return this.post<Approval>(`api/approvals/${id}/${approve ? 'approve' : 'reject'}`, { note }); }
  approvalRules() { return this.get<ApprovalRule[]>('api/approvals/rules'); }
  setApprovalRule(r: ApprovalRule) { return this.put('api/approvals/rules', r); }

  // ---- sales channels
  channels() { return this.get<Channel[]>('api/commerce/channels'); }
  createChannel(key: string, name: string, model: string, commissionPct: number, reservationMinutes: number) {
    return this.post<{ id: number; key: string; apiKey: string }>('api/commerce/channels', { key, name, model, commissionPct, reservationMinutes });
  }
  listings(channelId: number) { return this.get<Listing[]>(`api/commerce/channels/${channelId}/listings`); }
  publish(channel: string, productIds: number[]) { return this.post<PublishResult[]>('api/commerce/publish', { channel, productIds }); }
  withdraw(listingId: number) { return this.post(`api/commerce/listings/${listingId}/withdraw`); }

  // ---- returns
  creditNotes(invoiceId?: number) { return this.get<CreditNote[]>('api/sales/credit-notes', { invoiceId }); }
  returnItems(invoiceId: number, reason: string, restock: boolean, lines: { productId: number; quantity: number }[], warehouseId?: number | null) {
    return this.post<{ id: number; number: string; total: number; appliedToInvoice: number; creditToCustomer: number }>(`api/sales/${invoiceId}/returns`,
      { reason, restock, lines, warehouseId: warehouseId ?? null });
  }
}

export const MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
export const today = () => new Date().toLocaleDateString('en-CA');
export const firstOfMonth = () => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1).toLocaleDateString('en-CA'); };
export const firstOfYear = () => `${new Date().getFullYear()}-01-01`;
