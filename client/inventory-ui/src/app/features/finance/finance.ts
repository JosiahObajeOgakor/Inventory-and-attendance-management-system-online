import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api, messageOf } from '../../core/api.service';
import { FinanceSummary, LedgerRow, MonthlyIncome } from '../../core/models';
import { Icon } from '../../shared/icon';
import { PagedList } from '../../shared/paged-list';
import { DayPipe, NairaPipe, Pager } from '../../shared/ui';

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

@Component({
  selector: 'app-finance',
  imports: [Icon, NairaPipe, DayPipe, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Finance</h1>
        <div class="actions">
          <label class="sr-only" for="ym">Month</label>
          <select id="ym" class="input sel" (change)="setMonth($any($event.target).value)">
            @for (m of months; track m.value) { <option [value]="m.value" [selected]="m.value === selectedMonth()">{{ m.label }}</option> }
          </select>
          <a class="btn" [href]="exportUrl('report')" download><app-icon name="download" [size]="18" /> Report (Excel)</a>
          <a class="btn" [href]="exportUrl('ledger')" download><app-icon name="download" [size]="18" /> Ledger (Excel)</a>
          <a class="btn" [href]="exportUrl('full')" download><app-icon name="download" [size]="18" /> Everything (Excel)</a>
        </div>
      </div>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (s(); as s) {
        <div class="grid cols-4">
          <div class="card kpi"><span class="eyebrow">Sales before discounts</span><div class="value money">{{ s.revenue | naira }}</div><div class="sub">Discounts given: {{ s.discounts | naira }}</div></div>
          <div class="card kpi"><span class="eyebrow">Cost of goods sold</span><div class="value money">{{ s.cogs | naira }}</div></div>
          <div class="card kpi"><span class="eyebrow">Gross profit</span><div class="value money">{{ s.grossProfit | naira }}</div></div>
          <div class="card kpi" [class.warn]="s.netProfit < 0"><span class="eyebrow">Profit after expenses</span><div class="value money">{{ s.netProfit | naira }}</div><div class="sub">Expenses: {{ s.expenses | naira }}</div></div>
        </div>
        <div class="grid cols-3 gap">
          <div class="card kpi"><span class="eyebrow">Customers owe us</span><div class="value money">{{ s.accountsReceivable | naira }}</div></div>
          <div class="card kpi"><span class="eyebrow">We owe suppliers</span><div class="value money">{{ s.accountsPayable | naira }}</div></div>
          <div class="card kpi"><span class="eyebrow">Rebates accrued, not yet redeemed</span><div class="value money">{{ s.rebatesAvailable | naira }}</div></div>
        </div>
      }

      <section class="card gap">
        <h2>Income by month, {{ year() }}</h2>
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Month</th><th class="num">Invoices</th><th class="num">Gross sales</th><th class="num">VAT</th><th class="num">Net sales</th><th class="num">Collected</th><th class="num">Outstanding</th></tr></thead>
          <tbody>
            @for (m of income(); track m.month) {
              <tr><td class="strong">{{ monthName(m.month) }}</td><td class="num mono">{{ m.invoices }}</td><td class="num mono">{{ m.grossSales | naira }}</td><td class="num mono">{{ m.vat | naira }}</td>
                <td class="num mono">{{ m.netSales | naira }}</td><td class="num mono">{{ m.collected | naira }}</td><td class="num mono" [class.owes]="m.outstanding > 0">{{ m.outstanding | naira }}</td></tr>
            }
          </tbody>
        </table></div>
        @if (!income().length) { <div class="empty"><strong>No sales in {{ year() }}</strong>Income appears here as invoices are recorded.</div> }
      </section>

      <section class="card gap">
        <h2>Customer and supplier ledger</h2>
        <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
          <input class="input" type="search" placeholder="Search account or reference" aria-label="Search ledger" (input)="ledger.setSearch($any($event.target).value)" /></div></div>
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Date</th><th>Account</th><th>Type</th><th>Reference</th><th class="num">Debit</th><th class="num">Credit</th></tr></thead>
          <tbody>
            @for (l of ledger.items(); track l.id) {
              <tr><td>{{ l.entryDate | day }}</td><td class="strong">{{ l.accountName }}</td><td>{{ l.accountType }}</td><td class="mono">{{ l.reference }}</td>
                <td class="num mono">{{ l.entryType === 'Debit' ? (l.amount | naira) : '' }}</td><td class="num mono">{{ l.entryType === 'Credit' ? (l.amount | naira) : '' }}</td></tr>
            }
          </tbody>
        </table></div>
        @if (!ledger.loading() && !ledger.items().length) { <div class="empty"><strong>No ledger entries</strong>Credit sales, payments and supplier bills are posted here.</div> }
        <app-pager [page]="ledger.page()" [pageSize]="ledger.pageSize" [total]="ledger.total()" (pageChange)="ledger.goTo($event)" />
      </section>
    </div>`,
  styles: `.gap { margin-top: 1rem; } .sel { width: 12rem; } .owes { color: var(--stamp); font-weight: 600; }`,
})
export class FinancePage implements OnInit {
  private readonly api = inject(Api);
  protected readonly s = signal<FinanceSummary | null>(null);
  protected readonly income = signal<MonthlyIncome[]>([]);
  protected readonly error = signal('');
  protected readonly year = signal(new Date().getFullYear());
  protected readonly ledger = new PagedList<LedgerRow>(q => this.api.ledger(q), 12);

  protected readonly months = (() => {
    const out: { value: string; label: string }[] = []; const now = new Date();
    for (let i = 0; i < 12; i++) { const d = new Date(now.getFullYear(), now.getMonth() - i, 1); out.push({ value: `${d.getFullYear()}-${d.getMonth() + 1}`, label: `${MONTHS[d.getMonth()]} ${d.getFullYear()}` }); }
    return out;
  })();
  protected readonly selectedMonth = signal(this.months[0].value);
  protected monthName(m: number) { return MONTHS[m - 1]; }

  ngOnInit() { void this.load(); void this.ledger.load(); }

  /** The selected month, first to last day, as the server's export endpoints expect. */
  protected exportUrl(kind: 'report' | 'ledger' | 'full'): string {
    const [y, m] = this.selectedMonth().split('-').map(Number);
    const pad = (n: number) => String(n).padStart(2, '0');
    const last = new Date(y, m, 0).getDate();
    return `/api/exports/${kind}?from=${y}-${pad(m)}-01&to=${y}-${pad(m)}-${pad(last)}`;
  }

  protected setMonth(v: string) { this.selectedMonth.set(v); this.year.set(Number(v.split('-')[0])); void this.load(); }

  private async load() {
    const [y, m] = this.selectedMonth().split('-').map(Number);
    try {
      const [s, inc] = await Promise.all([this.api.financeSummary(y, m), this.api.income(y)]);
      this.s.set(s); this.income.set(inc); this.error.set('');
    } catch (e) { this.error.set(messageOf(e)); }
  }
}
