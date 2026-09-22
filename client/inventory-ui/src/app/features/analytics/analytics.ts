import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { BasketRule, ForecastRow, UnusualRow } from '../../core/models-dash';
import { DayPipe, StampTimePipe } from '../../shared/ui';

type Tab = 'forecast' | 'together' | 'unusual';

/**
 * The desktop app's stock predictions, on the web. Every figure is worked out from this company's own sales and stock records by fixed rules:
 * the same records always give the same answer, and each forecast says how far off that method has actually been on weeks it hadn't seen.
 */
@Component({
  selector: 'app-analytics',
  imports: [DayPipe, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Analytics</h1></div>
      <p class="muted lede">Forecasts, products that sell together, and stock movements that don’t look like the others. Worked out from your own records, never guessed.</p>
      <div class="tabs" role="tablist">
        @for (t of tabs; track t.id) { <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === t.id" (click)="select(t.id)">{{ t.label }}</button> }
      </div>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (tab() === 'forecast') {
        <section class="card">
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Product</th><th class="num">Per week</th><th class="num">On hand</th><th>Runs out</th><th class="num">Order</th><th>How sure</th></tr></thead>
            <tbody>@for (f of forecast(); track f.productId) {
              <tr><td class="strong">{{ f.product }}<div class="muted sm">{{ f.summary }}</div></td><td class="num mono">{{ f.weeklyDemand }}</td><td class="num mono">{{ f.onHand }} <span class="muted">{{ f.unit }}</span></td>
                <td>@if (f.runsOutOn) { {{ f.runsOutOn | day }}<div class="muted sm">{{ f.daysOfCover }} days of cover</div> } @else { — }</td>
                <td class="num mono" [class.need]="f.suggestedOrder > 0">{{ f.suggestedOrder || '—' }}</td>
                <td><span class="conf" [class]="'conf ' + f.confidence.toLowerCase()">{{ f.confidence }}</span></td></tr>
            }</tbody>
          </table></div>
          @if (loaded() && !forecast().length) { <div class="empty"><strong>Nothing to forecast yet</strong>Forecasts appear once products have a few weeks of sales.</div> }
          <p class="foot">Order = what to buy to cover a {{ 4 }}-week delivery. “How sure” is measured: each method is tested on recent weeks it hadn’t seen, and the best one is used.</p>
        </section>
      }

      @if (tab() === 'together') {
        <section class="card">
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Customers who buy…</th><th>…also take</th><th class="num">How often</th><th class="num">Above chance</th><th class="num">Sales behind it</th></tr></thead>
            <tbody>@for (r of rules(); track $index) {
              <tr><td class="strong">{{ r.antecedent.join(' + ') }}</td><td>{{ r.consequent }}</td><td class="num mono">{{ (r.confidence * 100).toFixed(0) }}%</td>
                <td class="num mono"><b [class.strong-lift]="r.lift >= 1.3">{{ r.lift.toFixed(1) }}×</b></td><td class="num mono">{{ r.baskets }}</td></tr>
            }</tbody>
          </table></div>
          @if (loaded() && !rules().length) { <div class="empty"><strong>No pairs found yet</strong>It needs at least two sales that contain the same pair.</div> }
          <p class="foot">Above 1.3× is a real pattern; near 1× just means both products are popular. Use it for shelf placement, bundles and what to reorder together.</p>
        </section>
      }

      @if (tab() === 'unusual') {
        <section class="card">
          <div class="table-wrap"><table class="table">
            <thead><tr><th>When</th><th>Product</th><th>Movement</th><th class="num">Quantity</th><th class="num">Usually</th><th class="num">Strangeness</th></tr></thead>
            <tbody>@for (u of unusual(); track u.movementId) {
              <tr><td>{{ u.at | stampTime }}</td><td class="strong">{{ u.product }}<div class="muted sm">{{ u.warehouse }} · {{ u.reference }}</div></td><td>{{ u.kind }}</td>
                <td class="num mono need">{{ u.quantity }}</td><td class="num mono">{{ u.typical }}</td><td class="num mono">{{ u.score }}</td></tr>
            }</tbody>
          </table></div>
          @if (loaded() && !unusual().length) { <div class="empty"><strong>Nothing looks odd</strong>Every recent movement is in line with what usually goes in or out.</div> }
          <p class="foot">Only quantities larger than usual are flagged, for products with at least a dozen past movements. A flag is a reason to check, not proof of a mistake.</p>
        </section>
      }
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 46rem; } .sm { font-size: .75rem; } .need { color: var(--stamp); font-weight: 600; } .foot { margin: 0; padding: .8rem 1.125rem; font-size: .78rem; color: var(--muted); border-top: 1px solid var(--line); }
    .conf { font-size: .72rem; font-weight: 700; padding: .15rem .5rem; border-radius: 99px; background: #eef1f6; } .conf.high { background: #e6f7ee; color: #0a8f4d; } .conf.medium { background: #fff3dc; color: #8a5a00; } .conf.low { background: #fdeceb; color: #b3261e; }
    .strong-lift { color: #0a8f4d; }
  `,
})
export class AnalyticsPage implements OnInit {
  private readonly api = inject(Api2);
  protected readonly tabs: { id: Tab; label: string }[] = [{ id: 'forecast', label: 'Demand forecast' }, { id: 'together', label: 'Sold together' }, { id: 'unusual', label: 'Unusual movements' }];
  protected readonly tab = signal<Tab>('forecast');
  protected readonly forecast = signal<ForecastRow[]>([]);
  protected readonly rules = signal<BasketRule[]>([]);
  protected readonly unusual = signal<UnusualRow[]>([]);
  protected readonly loaded = signal(false);
  protected readonly error = signal('');

  ngOnInit() { void this.select('forecast'); }

  protected async select(t: Tab) {
    this.tab.set(t); this.loaded.set(false); this.error.set('');
    try {
      if (t === 'forecast') this.forecast.set(await this.api.forecast());
      else if (t === 'together') this.rules.set(await this.api.soldTogether());
      else this.unusual.set(await this.api.unusualMovements());
      this.loaded.set(true);
    } catch (e) { this.error.set(messageOf(e)); }
  }
}
