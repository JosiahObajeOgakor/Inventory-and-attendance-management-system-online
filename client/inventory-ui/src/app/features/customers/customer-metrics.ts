import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { CustomerMetrics } from '../../core/models-more';
import { Modal } from '../../shared/modal';
import { DayPipe, NairaPipe, Stamp } from '../../shared/ui';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Read-only purchase history for one customer: twelve months of spend, what they buy most, and how they pay (replaces the desktop "Customer metrics" window). */
@Component({
  selector: 'app-customer-metrics',
  imports: [Modal, NairaPipe, DayPipe, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-modal [open]="customerId() !== null" [heading]="m()?.name ?? 'Customer'" [wide]="true" (closed)="closed.emit()">
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      @if (m(); as m) {
        <div class="kpis">
          <div><span class="eyebrow">Ranking</span><app-stamp [label]="m.ranking" /></div>
          <div><span class="eyebrow">Last 12 months</span><strong class="mono">{{ m.trailingTwelveMonths | naira }}</strong></div>
          <div><span class="eyebrow">Orders</span><strong class="mono">{{ m.invoiceCount }}</strong></div>
          <div><span class="eyebrow">Average order</span><strong class="mono">{{ m.averageOrder | naira }}</strong></div>
          <div><span class="eyebrow">Last bought</span><strong>{{ m.lastPurchase | day }}</strong></div>
          <div><span class="eyebrow">Owes now</span><strong class="mono" [class.owes]="m.balance > 0">{{ m.balance | naira }}</strong></div>
        </div>

        <h3 class="sub">Spend by month</h3>
        <div class="bars" role="img" [attr.aria-label]="'Monthly spend, highest ' + (max() | naira)">
          @for (b of m.monthly; track $index) {
            <div class="bar" [title]="label(b) + ': ' + (b.total | naira) + ' in ' + b.invoices + ' order(s)'">
              <span class="fill" [style.height.%]="max() ? b.total / max() * 100 : 0"></span><small>{{ label(b) }}</small>
            </div>
          }
        </div>

        <h3 class="sub">Most bought products</h3>
        @if (m.topProducts.length) {
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Product</th><th class="num">Units</th><th class="num">Revenue</th></tr></thead>
            <tbody>@for (p of m.topProducts; track p.productId) { <tr><td class="strong">{{ p.product }}</td><td class="num mono">{{ p.quantity }}</td><td class="num mono">{{ p.revenue | naira }}</td></tr> }</tbody>
          </table></div>
        } @else { <p class="muted">No purchases yet.</p> }
      } @else if (!error()) { <div class="skeleton" style="height:16rem"></div> }
    </app-modal>`,
  styles: `
    .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 1rem; } .kpis div { display: flex; flex-direction: column; gap: .2rem; } .owes { color: var(--stamp); }
    .sub { margin: 1.4rem 0 .6rem; font: 600 .9rem var(--font-body); } .bars { display: flex; align-items: flex-end; gap: .4rem; height: 9rem; }
    .bar { flex: 1; height: 100%; display: flex; flex-direction: column; justify-content: flex-end; align-items: center; gap: .25rem; } .fill { width: 100%; background: var(--brand); border-radius: 4px 4px 0 0; min-height: 2px; } .bar small { font-size: .65rem; color: var(--muted); }
  `,
})
export class CustomerMetricsDialog {
  private readonly api = inject(Api2);
  readonly customerId = input<number | null>(null);
  readonly closed = output<void>();
  protected readonly m = signal<CustomerMetrics | null>(null);
  protected readonly error = signal('');

  constructor() {
    effect(() => {
      const id = this.customerId();
      untracked(async () => {
        this.m.set(null); this.error.set('');
        if (id === null) return;
        try { this.m.set(await this.api.customerMetrics(id)); } catch (e) { this.error.set(messageOf(e)); }
      });
    });
  }

  protected max() { return Math.max(0, ...(this.m()?.monthly.map(b => b.total) ?? [])); }
  protected label(b: { year: number; month: number }) { return `${MONTHS[b.month - 1]} ${String(b.year).slice(2)}`; }
}
