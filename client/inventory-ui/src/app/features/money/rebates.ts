import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { RebateCustomer, RebateEntry, RebateSummary } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe, NairaPipe, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-rebates',
  imports: [FormsModule, Icon, NairaPipe, DayPipe, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Rebates</h1></div>
      <p class="muted lede">Every sale to a named customer earns them a percentage back, given later as goods. This is what each customer has earned and what has been handed over.</p>

      @if (s(); as s) {
        <div class="kpis">
          <div class="card kpi"><span class="eyebrow">Not yet collected</span><div class="value money">{{ s.outstandingTotal | naira }}</div></div>
          <div class="card kpi"><span class="eyebrow">Already given back</span><div class="value money">{{ s.redeemedTotal | naira }}</div></div>
          <div class="card kpi rate"><label class="eyebrow" for="rr">Default rate for new customers (%)</label>
            <div class="rate-row"><input id="rr" class="input num" type="number" min="0" max="100" step="0.25" [ngModel]="rate()" (ngModelChange)="rate.set($event)" />
              <button type="button" class="btn btn-sm" (click)="saveRate()">Save rate</button></div></div>
        </div>
      }

      <div class="split">
        <section class="card">
          <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search customer or ranking" aria-label="Search rebates" (input)="onSearch($any($event.target).value)" /></div></div>
          @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Customer</th><th class="num">Not yet collected</th><th class="num">Already received</th><th class="num">Lifetime</th></tr></thead>
            <tbody>
              @for (c of s()?.customers ?? []; track c.customerId) {
                <tr class="pick" [class.on]="sel()?.customerId === c.customerId" tabindex="0" (click)="select(c)" (keydown.enter)="select(c)">
                  <td><span class="strong">{{ c.customer }}</span> <app-stamp [label]="c.ranking" /></td>
                  <td class="num mono" [class.owes]="c.available > 0">{{ c.available | naira }}</td><td class="num mono">{{ c.redeemed | naira }}</td><td class="num mono">{{ c.lifetime | naira }}</td>
                </tr>
              }
            </tbody>
          </table></div>
          @if (!loading() && !(s()?.customers?.length)) { <div class="empty"><strong>No rebates yet</strong>They build up as customers buy.</div> }
        </section>

        <section class="card detail">
          @if (sel(); as c) {
            <div class="toolbar"><strong class="grow">{{ c.customer }}</strong>
              <button type="button" class="btn btn-primary btn-sm" [disabled]="c.available <= 0" (click)="redeem(c)">Give back {{ c.available | naira }}</button></div>
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Date</th><th>Sale</th><th class="num">Amount</th><th>Status</th></tr></thead>
              <tbody>@for (e of entries(); track e.id) {
                <tr><td>{{ e.entryDate | day }}</td><td class="mono">{{ e.invoiceNumber ?? '—' }}</td><td class="num mono">{{ e.amount | naira }}</td>
                  <td><app-stamp [label]="e.status" />@if (e.redeemedDate) { <div class="muted sm">{{ e.redeemedDate | day }}</div> }</td></tr>
              }</tbody>
            </table></div>
          } @else { <div class="empty"><strong>Choose a customer</strong>Their rebate entries appear here.</div> }
        </section>
      </div>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 46rem; } .sm { font-size: .75rem; } .owes { color: var(--stamp); font-weight: 600; }
    .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: 1rem; margin-bottom: 1rem; }
    .rate-row { display: flex; gap: .5rem; margin-top: .5rem; } .rate-row .input { max-width: 7rem; }
    .split { display: grid; grid-template-columns: minmax(0, 3fr) minmax(0, 2fr); gap: 1rem; align-items: start; }
    .pick { cursor: pointer; } .pick.on { background: var(--brand-tint); } .pick:focus-visible { outline: 2px solid var(--brand); outline-offset: -2px; }
    @media (max-width: 1000px) { .split { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class RebatesPage implements OnInit {
  private readonly api = inject(Api2);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly s = signal<RebateSummary | null>(null);
  protected readonly rate = signal(1);
  protected readonly sel = signal<RebateCustomer | null>(null);
  protected readonly entries = signal<RebateEntry[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  private term = '';
  private timer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit() { void this.load(true); }

  private async load(first = false) {
    this.loading.set(true);
    try {
      const r = await this.api.rebates(this.term); this.s.set(r); if (first) this.rate.set(r.defaultRatePct); this.error.set('');
      const cur = this.sel(); if (cur) this.sel.set(r.customers.find(c => c.customerId === cur.customerId) ?? null);
    } catch (e) { this.error.set(messageOf(e)); } finally { this.loading.set(false); }
  }

  protected onSearch(v: string) { this.term = v; if (this.timer) clearTimeout(this.timer); this.timer = setTimeout(() => void this.load(), 300); }

  protected async select(c: RebateCustomer) {
    this.sel.set(c);
    try { this.entries.set(await this.api.rebateEntries(c.customerId)); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async saveRate() {
    try { await this.api.setRebateRate(Number(this.rate())); this.toasts.ok(`Default rebate rate set to ${this.rate()}%.`); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async redeem(c: RebateCustomer) {
    const ok = await this.confirm.ask({ title: `Give back ${c.customer}’s rebate?`, message: `This marks ₦${c.available.toLocaleString('en-NG', { minimumFractionDigits: 2 })} as handed over as goods. Record the goods themselves separately, as a zero-price sale or a stock adjustment.`, confirmLabel: 'Mark as given back' });
    if (ok === null) return;
    try { const r = await this.api.redeemRebate(c.customerId); this.toasts.ok(`₦${r.amount.toLocaleString('en-NG')} rebate given back to ${r.customer}.`); await this.load(); await this.select(this.sel() ?? c); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
