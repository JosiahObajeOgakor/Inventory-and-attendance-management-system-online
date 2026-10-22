import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface VatLine { date: string; reference: string; party: string; net: number; vat: number; }
export interface VatReturn { year: number; month: number; outputVat: number; inputVat: number; netPayable: number; creditBroughtForward: number; payable: number;
  sales: VatLine[]; purchases: VatLine[]; expenses: VatLine[]; returns: VatLine[]; dueDate: string; filed: boolean; amountPaid: number; reference: string | null; }
export interface WhtRate { id: number; code: string; name: string; companyPct: number; individualPct: number; active: boolean; }
export interface WhtDeduction { id: number; date: string; payeeName: string; payeeTin: string | null; payeeIsIndividual: boolean; category: string; base: number; ratePct: number;
  amount: number; sourceType: string; sourceId: number; authority: string; certificateNumber: string; }
export interface WhtRow { authority: string; payees: number; withheld: number; remitted: number; outstanding: number; dueDate: string; }
export interface WhtMonth { year: number; month: number; authorities: WhtRow[]; deductions: WhtDeduction[]; }
export interface WhtCertificate { company: string; companyTin: string | null; deduction: WhtDeduction; supplierAddress: string | null; }

export interface BankAccount { id: number; name: string; bankName: string; accountNumber: string; glCode: string; currency: string; active: boolean; ledgerBalance: number;
  unmatched: number; lastReconciled: string | null; }
export interface StatementLine { id: number; date: string; description: string; reference: string | null; amount: number; runningBalance: number | null; status: string;
  glLineId: number | null; matchedTo: string | null; }
export interface LedgerItem { glLineId: number; journalId: number; date: string; memo: string; source: string; amount: number; cleared: boolean; }
export interface Reconciliation { id: number; statementDate: string; statementBalance: number; ledgerBalance: number; unclearedReceipts: number; unclearedPayments: number;
  status: string; completedBy: string | null; completedAt: string | null; }
export interface ReconcileView { account: BankAccount; asOf: string; statementBalance: number; ledgerBalance: number; unclearedReceipts: number; unclearedPayments: number;
  adjustedBankBalance: number; difference: number; statement: StatementLine[]; uncleared: LedgerItem[]; history: Reconciliation[]; }
export interface ImportResult { imported: number; duplicates: number; autoMatched: number; problems: string[]; }

/** Tax (VAT, withholding tax) and banking (accounts, statements, reconciliation). */
@Injectable({ providedIn: 'root' })
export class FinanceApi {
  private readonly http = inject(HttpClient);
  private get<T>(u: string, p?: Record<string, string | number | undefined | null>) {
    const clean = Object.fromEntries(Object.entries(p ?? {}).filter(([, v]) => v !== undefined && v !== null && v !== '')) as Record<string, string | number>;
    return firstValueFrom(this.http.get<T>(u, { params: new HttpParams({ fromObject: clean }) }));
  }
  private post<T>(u: string, body: unknown = {}) { return firstValueFrom(this.http.post<T>(u, body)); }
  private put(u: string, body: unknown) { return firstValueFrom(this.http.put<void>(u, body)); }

  vat(year: number, month: number) { return this.get<VatReturn>(`api/tax/vat/${year}/${month}`); }
  fileVat(year: number, month: number, paidOn: string | null, reference: string | null) { return this.post<{ id: number }>('api/tax/vat/file', { year, month, paidOn, reference }); }
  whtRates() { return this.get<WhtRate[]>('api/tax/wht/rates'); }
  saveWhtRate(r: WhtRate) { return this.put('api/tax/wht/rates', r); }
  wht(year: number, month: number) { return this.get<WhtMonth>(`api/tax/wht/${year}/${month}`); }
  remitWht(year: number, month: number, authority: string, amount: number, paidOn: string | null, reference: string | null) {
    return this.post('api/tax/wht/remit', { year, month, authority, amount, paidOn, reference });
  }
  whtCertificate(id: number) { return this.get<WhtCertificate>(`api/tax/wht/certificates/${id}`); }

  bankAccounts() { return this.get<BankAccount[]>('api/banking/accounts'); }
  addBankAccount(name: string, bankName: string, accountNumber: string) { return this.post<{ id: number }>('api/banking/accounts', { name, bankName, accountNumber }); }
  importStatement(id: number, file: File) { const f = new FormData(); f.append('file', file); return this.post<ImportResult>(`api/banking/accounts/${id}/import`, f); }
  autoMatch(id: number) { return this.post<{ matched: number }>(`api/banking/accounts/${id}/auto-match`); }
  reconcile(id: number, asOf?: string, statementBalance?: number | null) { return this.get<ReconcileView>(`api/banking/accounts/${id}/reconcile`, { asOf, statementBalance }); }
  complete(id: number, statementDate: string, statementBalance: number) { return this.post(`api/banking/accounts/${id}/reconcile`, { statementDate, statementBalance }); }
  match(lineId: number, glLineId: number) { return this.post(`api/banking/lines/${lineId}/match`, { glLineId }); }
  unmatch(lineId: number) { return this.post(`api/banking/lines/${lineId}/unmatch`); }
  ignore(lineId: number) { return this.post(`api/banking/lines/${lineId}/ignore`); }
  postLine(lineId: number, accountCode: string, memo: string | null) { return this.post<{ journalId: number }>(`api/banking/lines/${lineId}/post`, { accountCode, memo }); }

  async download(path: string, name: string) {
    const blob = await firstValueFrom(this.http.get(path, { responseType: 'blob' }));
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }
}
