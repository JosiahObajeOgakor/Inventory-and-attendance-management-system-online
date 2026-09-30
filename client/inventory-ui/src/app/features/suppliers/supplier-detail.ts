import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { SupplierStatement, SupplierStatementOrder } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { asNumber } from '../../shared/paged-list';
import { SendDocument } from '../../shared/send-document';
import { DayPipe, NairaPipe, Stamp, StampTimePipe } from '../../shared/ui';

/**
 * One supplier, everything tied to them: what they sell us (and what it costs), every order with what was paid and what is still owed,
 * every payment we made, and the running total we owe. From here you start a new order with them, pay them, or send them an order.
 */
@Component({
  selector: 'app-supplier-detail',
  imports: [RouterLink, FormsModule, Icon, Modal, SendDocument, NairaPipe, DayPipe, StampTimePipe, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>{{ st()?.supplier?.name ?? 'Supplier' }}</h1>
          @if (st(); as s) { <p class="muted contact">{{ contact() }}</p> }
        </div>
        <div class="actions">
          <a class="btn" routerLink="/suppliers">All suppliers</a>
          @if (st(); as s) {
            @if (s.owed > 0) { <button type="button" class="btn" (click)="openPay()"><app-icon name="cash" [size]="18" /> Pay supplier</button> }
            <a class="btn btn-primary" routerLink="/purchases/new" [queryParams]="{ supplier: s.supplier.id }"><app-icon name="plus" [size]="18" /> New purchase</a>
          }
        </div>
      </div>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (st(); as s) {
        <section class="tiles">
          <article class="card tile"><span>Total bought</span><strong class="mono">{{ s.totalBought | naira }}</strong><small>{{ s.orders }} order(s){{ s.lastOrder ? ' · last ' + (s.lastOrder | day) : '' }}</small></article>
          <article class="card tile"><span>Total paid</span><strong class="mono">{{ s.totalPaid | naira }}</strong><small>{{ s.payments.length }} payment(s)</small></article>
          <article class="card tile" [class.owes]="s.owed > 0"><span>{{ s.owed < 0 ? 'Credit with them' : 'We owe' }}</span><strong class="mono">{{ (s.owed < 0 ? -s.owed : s.owed) | naira }}</strong>
            <small>{{ s.openOrders ? s.openOrders + ' unpaid order(s)' : 'Nothing outstanding' }}</small></article>
        </section>

        <section class="card">
          <div class="c-head"><h2>Items they supply</h2><span class="muted">Usual cost is what a new purchase starts from. Edit the list from All suppliers › Items.</span></div>
          @if (s.items.length) {
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Product</th><th>Unit</th><th class="num">Usual cost</th><th class="num">Last paid</th><th class="num">Qty bought</th><th class="num">Spent</th></tr></thead>
              <tbody>@for (it of s.items; track it.productId) {
                <tr [class.off]="!it.isActive"><td><span class="strong">{{ it.product }}</span><div class="muted mono sm">{{ it.sku }}{{ it.onList ? '' : ' · not on their list' }}</div></td>
                  <td>{{ it.unit }}</td><td class="num mono">{{ it.usualCost | naira }}</td>
                  <td class="num mono">{{ it.lastCost === null ? '—' : (it.lastCost | naira) }}<div class="muted sm">{{ it.lastBought ? (it.lastBought | day) : '' }}</div></td>
                  <td class="num mono">{{ it.quantityBought }}</td><td class="num mono">{{ it.amountBought | naira }}</td></tr>
              }</tbody>
            </table></div>
          } @else { <div class="empty"><strong>No items yet</strong>Add what you buy from them under All suppliers › Items, or choose them as the supplier on a product.</div> }
        </section>

        <section class="card">
          <div class="c-head"><h2>Supplies</h2><span class="muted">What they supplied, recorded on its own — these figures never touch stock or the orders below.</span>
            <a class="btn btn-sm btn-primary" style="margin-left:auto" routerLink="/supplies">Record a supply</a></div>
          <div class="sup-tiles">
            <div class="sup-tile"><span>Supplied, all time</span><strong class="mono">{{ s.suppliedTotal | naira }}</strong><small>{{ s.supplyRecords }} record(s)</small></div>
            <div class="sup-tile" [class.owes]="s.suppliedOwed > 0"><span>Owed on supplies</span><strong class="mono">{{ s.suppliedOwed | naira }}</strong><small>Separate from the order balance</small></div>
          </div>
          @if (supplyMonths().length) {
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Month</th><th class="num">Records</th><th class="num">Supplied</th><th class="num">Owed</th></tr></thead>
              <tbody>@for (m of supplyMonths(); track m.year * 100 + m.month) {
                <tr><td>{{ monthName(m.month) }} {{ m.year }}</td><td class="num mono">{{ m.records }}</td>
                  <td class="num mono">{{ m.amount | naira }}</td><td class="num mono" [class.owes]="m.owed > 0">{{ m.owed | naira }}</td></tr>
              }</tbody>
            </table></div>
          } @else { <div class="empty"><strong>Nothing supplied yet</strong>Record what they bring under Supplies.</div> }
        </section>

        <section class="card">
          <div class="c-head"><h2>Orders (stock purchases)</h2><span class="muted">Every purchase from this supplier that went through stock, newest first.</span></div>
          @if (s.orderList.length) {
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Order</th><th>Date</th><th>Status</th><th class="num">Total</th><th class="num">Paid</th><th class="num">Owed</th><th><span class="sr-only">Actions</span></th></tr></thead>
              <tbody>@for (o of s.orderList; track o.id) {
                <tr><td><a class="strong mono" [routerLink]="['/purchases', o.id]">{{ o.poNumber }}</a><div class="muted sm">{{ o.lines }} line(s) · {{ o.units }} unit(s)</div></td>
                  <td>{{ o.orderDate | day }}</td><td><span class="stamps"><app-stamp [label]="o.status" /><app-stamp [label]="o.paymentStatus" /></span></td>
                  <td class="num mono">{{ o.total | naira }}</td><td class="num mono">{{ o.paid | naira }}</td><td class="num mono" [class.owes]="o.outstanding > 0">{{ o.outstanding | naira }}</td>
                  <td class="actions">@if (o.status !== 'Cancelled') { <button type="button" class="btn btn-sm" (click)="sending.set(o)"><app-icon name="send" [size]="15" /> Send</button> }</td></tr>
              }</tbody>
            </table></div>
          } @else { <div class="empty"><strong>No orders yet</strong>Start one with “New purchase”.</div> }
        </section>

        <section class="card">
          <div class="c-head"><h2>Payments</h2><span class="muted">What we have paid them, and which orders each payment settled.</span></div>
          @if (s.payments.length) {
            <div class="table-wrap"><table class="table">
              <thead><tr><th>When</th><th>Reference</th><th>Method</th><th>Orders paid</th><th class="num">Amount</th></tr></thead>
              <tbody>@for (p of s.payments; track p.reference + p.paidAt) {
                <tr><td>{{ p.method === 'Earlier' ? (p.paidAt.slice(0, 10) | day) : (p.paidAt | stampTime) }}</td><td class="mono">{{ p.reference }}</td><td>{{ p.method === 'Earlier' ? 'Recorded earlier' : p.method }}</td>
                  <td class="mono sm">{{ p.orders.join(', ') || '—' }}</td><td class="num mono">{{ p.amount | naira }}</td></tr>
              }</tbody>
            </table></div>
          } @else { <div class="empty"><strong>No payments yet</strong></div> }
        </section>
      } @else if (!error()) { <div class="card skeleton" style="height:20rem"></div> }
    </div>

    <app-modal [open]="payOpen()" [heading]="'Pay ' + (st()?.supplier?.name ?? '')" (closed)="payOpen.set(false)">
      <p>We owe them <strong class="mono">{{ (st()?.owed ?? 0) | naira }}</strong>. The payment settles their unpaid orders, oldest first.</p>
      <div class="form-grid" style="margin-top:.9rem">
        <div class="field"><label for="pa">Amount (₦)</label>
          <input id="pa" class="input num" type="number" min="0" step="0.01" [max]="st()?.owed ?? 0" [(ngModel)]="payAmount" /></div>
        <div class="field"><label for="pm">Paid by</label>
          <select id="pm" class="input" [(ngModel)]="payMethod"><option>Cash</option><option>Transfer</option><option>POS</option><option>Cheque</option></select></div>
      </div>
      <button type="button" class="btn btn-sm" style="margin-top:.6rem" (click)="payAmount = st()?.owed ?? 0">Pay all</button>
      @if (payError()) { <p class="notice bad" role="alert" style="margin-top:.8rem">{{ payError() }}</p> }
      <ng-container modal-actions><button type="button" class="btn" (click)="payOpen.set(false)">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !(amount() > 0)" (click)="pay()">{{ busy() ? 'Saving…' : 'Record payment' }}</button></ng-container>
    </app-modal>

    <app-send-document [open]="!!sending()" kind="purchases" [docId]="sending()?.id ?? null" [number]="sending()?.poNumber ?? ''"
      [defaultPhone]="st()?.supplier?.phone ?? ''" [defaultEmail]="st()?.supplier?.email ?? ''" (closed)="sending.set(null)" />`,
  styles: `
    .contact { margin: .2rem 0 0; font-size: .875rem; }
    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 1rem; margin-bottom: 1rem; }
    .tile { padding: 1rem 1.15rem; display: flex; flex-direction: column; gap: .35rem; } .tile span { color: var(--muted); font-size: .875rem; }
    .tile strong { font-size: 1.5rem; letter-spacing: -.03em; } .tile small { color: var(--muted); } .tile.owes strong, td.owes { color: var(--stamp); font-weight: 600; }
    section.card { margin-bottom: 1rem; } .c-head { display: flex; align-items: baseline; gap: .8rem; flex-wrap: wrap; padding: 1rem 1.15rem .6rem; } .c-head h2 { margin: 0; font-size: 1.05rem; } .c-head .muted { font-size: .8125rem; }
    .sm { font-size: .75rem; } .stamps { display: inline-flex; gap: .3rem; flex-wrap: wrap; } tr.off { opacity: .55; } td.actions { white-space: nowrap; }
    .sup-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: .8rem; padding: 0 1.15rem 1rem; }
    .sup-tile { display: flex; flex-direction: column; gap: .2rem; padding: .8rem 1rem; border: 1px solid var(--line); border-radius: var(--r-2); }
    .sup-tile span { color: var(--muted); font-size: .8125rem; } .sup-tile strong { font-size: 1.25rem; } .sup-tile small { color: var(--muted); font-size: .75rem; }
    .sup-tile.owes strong { color: var(--stamp); }
  `,
})
export class SupplierDetail implements OnInit {
  readonly id = input.required<string>();   // bound from the route
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);

  protected readonly st = signal<SupplierStatement | null>(null);
  protected readonly error = signal('');
  protected readonly busy = signal(false);
  protected readonly payOpen = signal(false);
  protected readonly payError = signal('');
  protected readonly sending = signal<SupplierStatementOrder | null>(null);
  protected payAmount: number | string = 0;
  protected payMethod = 'Cash';
  protected amount() { return asNumber(String(this.payAmount)); }

  private static readonly MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  protected monthName(month: number) { return SupplierDetail.MONTHS[month - 1] ?? ''; }
  /** Only the months this supplier actually supplied in — an empty year of zero rows tells nobody anything. */
  protected readonly supplyMonths = computed(() => (this.st()?.supplyMonths ?? []).filter(m => m.records > 0).reverse());

  protected readonly contact = computed(() => {
    const s = this.st()?.supplier; if (!s) return '';
    return [s.category, s.contactName, s.phone, s.email, s.address].filter(Boolean).join(' · ');
  });

  ngOnInit() { void this.load(); }

  private async load() {
    try { this.st.set(await this.api2.supplierStatement(Number(this.id()))); } catch (e) { this.error.set(messageOf(e)); }
  }

  protected openPay() { this.payAmount = this.st()?.owed ?? 0; this.payMethod = 'Cash'; this.payError.set(''); this.payOpen.set(true); }

  protected async pay() {
    const s = this.st(); if (!s) return;
    this.busy.set(true); this.payError.set('');
    try {
      const r = await this.api2.paySupplier(s.supplier.id, this.amount(), this.payMethod);
      const naira = new NairaPipe();
      this.toasts.ok(`Paid ${naira.transform(r.amount)}. ${r.balanceNow > 0 ? 'Still owed: ' + naira.transform(r.balanceNow) + '.' : 'Nothing more owed.'}`);
      this.payOpen.set(false); await this.load();
    } catch (e) { this.payError.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
