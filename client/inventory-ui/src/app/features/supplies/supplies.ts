import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Auth } from '../../core/auth.service';
import { Supplier } from '../../core/models';
import { SupplierProduct, SupplyDetail, SupplyRow } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { PagedList, asNumber } from '../../shared/paged-list';
import { DayPipe, NairaPipe, Pager, Stamp } from '../../shared/ui';
import { SupplierCatalog } from '../suppliers/supplier-catalog';

interface Picked { supplierProductId: number; name: string; unit: string; qty: number; cost: number; }

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

/**
 * What suppliers supplied — a record of its own. Choosing a supplier lists the items they supply; you type the quantity and price against each,
 * then save. Nothing here touches stock, a product's cost price or the ledger, so a supplier's figures stand on their own.
 */
@Component({
  selector: 'app-supplies',
  imports: [FormsModule, RouterLink, Icon, Modal, SupplierCatalog, NairaPipe, DayPipe, Pager, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Supplies</h1>
          <p class="crumb">{{ auth.companyName() }} <span>›</span> What suppliers supplied</p>
        </div>
        <div class="actions">
          <button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> Record a supply</button>
          @if (auth.canDelete()) { <button type="button" class="btn btn-danger" (click)="openClear()">Clear records</button> }
        </div>
      </div>

      <p class="notice">These records are kept separate from your stock and from purchase orders on purpose: recording a supply never changes stock
        levels or a product’s cost price. It answers one question only — what each supplier supplied, and what is still owed on it.</p>

      <section class="tiles">
        <article class="card tile"><span>Supplied this month</span><strong class="mono">{{ monthTotal() | naira }}</strong>
          <small>{{ rowsThisMonth() }} record(s) on this page</small></article>
        <article class="card tile" [class.owes]="pageOwed() > 0"><span>Owed on these records</span><strong class="mono">{{ pageOwed() | naira }}</strong>
          <small>Across the records listed below</small></article>
      </section>

      <section class="card">
        <div class="toolbar">
          <div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search reference or supplier" aria-label="Search supplies" (input)="list.setSearch($any($event.target).value)" /></div>
          <select class="input w-auto" aria-label="Filter by supplier" [ngModel]="filterSupplier()" (ngModelChange)="filterSupplier.set($event); reload()">
            <option [ngValue]="0">All suppliers</option>
            @for (s of suppliers(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }
          </select>
          <select class="input w-auto" aria-label="Filter by month" [ngModel]="filterMonth()" (ngModelChange)="filterMonth.set($event); reload()">
            <option [ngValue]="0">All months</option>
            @for (m of recentMonths(); track m.key) { <option [ngValue]="m.key">{{ m.label }}</option> }
          </select>
        </div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Reference</th><th>Supplier</th><th>Date</th><th class="num">Items</th><th class="num">Value</th><th class="num">Paid</th><th class="num">Owed</th><th>Status</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (s of list.items(); track s.id) {
              <tr>
                <td><button type="button" class="link mono strong" (click)="open(s)">{{ s.reference }}</button>
                  @if (s.note) { <div class="muted sm">{{ s.note }}</div> }</td>
                <td><a [routerLink]="['/suppliers', s.supplierId]">{{ s.supplier }}</a></td>
                <td>{{ s.supplyDate | day }}</td>
                <td class="num mono">{{ s.lines }} / {{ s.units }}</td>
                <td class="num mono">{{ s.totalAmount | naira }}</td>
                <td class="num mono">{{ s.amountPaid | naira }}</td>
                <td class="num mono" [class.owes]="s.outstanding > 0">{{ s.outstanding | naira }}</td>
                <td><app-stamp [label]="s.paymentStatus" /></td>
                <td class="actions">
                  @if (s.outstanding > 0) { <button type="button" class="btn btn-sm" (click)="openPay(s)">Pay</button> }
                  @if (auth.canDelete()) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(s)">Delete</button> }
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No supply records yet</strong>Use “Record a supply” after a supplier delivers.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <!-- record a supply -->
    <app-modal [open]="formOpen()" heading="Record a supply" [wide]="true" (closed)="formOpen.set(false)">
      <div class="form-grid">
        <div class="field"><label for="sv-s">Supplier</label>
          <select id="sv-s" class="input" [ngModel]="supplierId()" (ngModelChange)="supplierId.set(+$event)">
            <option [ngValue]="0">Choose a supplier…</option>
            @for (s of suppliers(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }
          </select></div>
        <div class="field"><label for="sv-d">Date supplied</label><input id="sv-d" class="input" type="date" [ngModel]="supplyDate()" (ngModelChange)="supplyDate.set($event)" /></div>
      </div>

      @if (supplierId() > 0) {
        <div class="sec-head">
          <h3 class="sec">Items {{ supplierName() }} supplies</h3>
          <button type="button" class="btn btn-sm" (click)="itemsOpen.set(true)"><app-icon name="plus" [size]="15" /> Manage items</button>
        </div>
        @if (loadingItems()) { <div class="skeleton" style="height:6rem"></div> }
        @else if (items().length) {
          <p class="muted sm">Type a quantity against what they brought. The price starts from their usual price — change it if this delivery cost something else.</p>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Item</th><th>Counted in</th><th class="num">Quantity</th><th class="num">Price each (₦)</th><th class="num">Line total</th></tr></thead>
            <tbody>
              @for (it of items(); track it.id) {
                <tr [class.on]="(qty()[it.id] ?? 0) > 0">
                  <td><span class="strong">{{ it.name }}</span>@if (it.size) { <div class="muted sm">{{ it.size }}</div> }</td>
                  <td>{{ it.unit }}</td>
                  <td class="num"><input class="input num w-qty" type="number" min="0" step="1" [attr.aria-label]="'Quantity of ' + it.name"
                    [value]="qty()[it.id] ?? ''" (input)="setQty(it.id, $any($event.target).value)" /></td>
                  <td class="num"><input class="input num w-cost" type="number" min="0" step="0.01" [attr.aria-label]="'Price of ' + it.name"
                    [value]="cost()[it.id] ?? it.unitCost" (input)="setCost(it.id, $any($event.target).value)" /></td>
                  <td class="num mono">{{ lineTotal(it) | naira }}</td>
                </tr>
              }
            </tbody>
          </table></div>
        } @else {
          <div class="empty"><strong>{{ supplierName() }} has no items listed</strong>
            <button type="button" class="btn btn-primary" (click)="itemsOpen.set(true)"><app-icon name="plus" [size]="17" /> Add items for {{ supplierName() }}</button></div>
        }

        <div class="form-grid" style="margin-top:1rem">
          <div class="field"><label for="sv-p">Paid now (₦)</label><input id="sv-p" class="input num" type="number" min="0" step="0.01" [ngModel]="paidNow()" (ngModelChange)="paidNow.set($event)" />
            <span class="hint">Leave 0 if nothing has been paid yet.</span></div>
          <div class="field"><label for="sv-m">Paid by</label>
            <select id="sv-m" class="input" [ngModel]="method()" (ngModelChange)="method.set($event)"><option>Cash</option><option>Transfer</option><option>POS</option><option>Cheque</option></select></div>
          <div class="field span-2"><label for="sv-n">Note <span class="muted">(optional)</span></label>
            <input id="sv-n" class="input" maxlength="400" placeholder="e.g. Delivered by their driver, 2 bags short" [ngModel]="note()" (ngModelChange)="note.set($event)" /></div>
        </div>

        <div class="sum">
          <span>{{ chosen().length }} item(s) · {{ totalUnits() }} unit(s)</span>
          <strong class="mono">{{ total() | naira }}</strong>
          @if (owedAfter() > 0) { <span class="owes">Owed after this: {{ owedAfter() | naira }}</span> }
        </div>
      }
      @if (formError()) { <p class="notice bad" role="alert" style="margin-top:.8rem">{{ formError() }}</p> }

      <ng-container modal-actions>
        <button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !chosen().length" (click)="save()">{{ busy() ? 'Saving…' : 'Save supply record' }}</button>
      </ng-container>
    </app-modal>

    <!-- one record -->
    <app-modal [open]="!!viewing()" [heading]="'Supply ' + (viewing()?.reference ?? '')" [wide]="true" (closed)="viewing.set(null)">
      @if (viewing(); as v) {
        <dl class="meta">
          <div><dt>Supplier</dt><dd>{{ v.supplier }}</dd></div>
          <div><dt>Date</dt><dd>{{ v.supplyDate | day }}</dd></div>
          <div><dt>Recorded by</dt><dd>{{ v.recordedBy }}</dd></div>
          <div><dt>Paid by</dt><dd>{{ v.paymentMethod ?? '—' }}</dd></div>
        </dl>
        @if (v.note) { <p class="muted">{{ v.note }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Line total</th></tr></thead>
          <tbody>@for (i of v.items; track i.name + $index) {
            <tr><td><span class="strong">{{ i.name }}</span>@if (i.size) { <div class="muted sm">{{ i.size }}</div> }</td>
              <td class="num mono">{{ i.quantity }} {{ i.unit }}</td><td class="num mono">{{ i.unitCost | naira }}</td><td class="num mono">{{ i.lineTotal | naira }}</td></tr>
          }</tbody>
        </table></div>
        <dl class="totals">
          <div><dt>Value supplied</dt><dd class="figure">{{ v.totalAmount | naira }}</dd></div>
          <div><dt>Paid</dt><dd class="mono">{{ v.amountPaid | naira }}</dd></div>
          @if (v.outstanding > 0) { <div class="owed"><dt>Still owed</dt><dd class="mono">{{ v.outstanding | naira }}</dd></div> }
        </dl>
      }
      <ng-container modal-actions><button type="button" class="btn" (click)="viewing.set(null)">Close</button></ng-container>
    </app-modal>

    <!-- pay one record -->
    <app-modal [open]="!!paying()" [heading]="'Pay ' + (paying()?.reference ?? '')" (closed)="paying.set(null)">
      <p>Still owed on this supply: <strong class="mono">{{ paying()?.outstanding ?? 0 | naira }}</strong></p>
      <div class="form-grid" style="margin-top:.9rem">
        <div class="field"><label for="pp-a">Amount (₦)</label><input id="pp-a" class="input num" type="number" min="0" step="0.01" [(ngModel)]="payAmount" /></div>
        <div class="field"><label for="pp-m">Paid by</label>
          <select id="pp-m" class="input" [(ngModel)]="payMethod"><option>Cash</option><option>Transfer</option><option>POS</option><option>Cheque</option></select></div>
      </div>
      <button type="button" class="btn btn-sm" style="margin-top:.6rem" (click)="payAmount = paying()?.outstanding ?? 0">Pay all</button>
      @if (payError()) { <p class="notice bad" role="alert" style="margin-top:.8rem">{{ payError() }}</p> }
      <ng-container modal-actions><button type="button" class="btn" (click)="paying.set(null)">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !(asNum(payAmount) > 0)" (click)="pay()">Record payment</button></ng-container>
    </app-modal>

    <app-supplier-catalog [open]="itemsOpen()" [supplierId]="supplierId()" [supplierName]="supplierName()"
      (closed)="itemsOpen.set(false)" (saved)="refreshItems()" />

    <!-- clear records (CEO) -->
    <app-modal [open]="clearOpen()" heading="Clear supply records" (closed)="clearOpen.set(false)">
      <p>This removes supply records only. Stock, products, suppliers, sales and purchase orders are untouched.</p>
      <div class="field" style="margin-top:.9rem"><label for="cl-w">What to clear</label>
        <select id="cl-w" class="input" [ngModel]="clearWhat()" (ngModelChange)="clearWhat.set($event)">
          <option value="supplier">Every record for one supplier</option>
          <option value="month">Every record in one month</option>
          <option value="all">Every supply record there is</option>
        </select></div>
      @if (clearWhat() === 'supplier') {
        <div class="field" style="margin-top:.7rem"><label for="cl-s">Supplier</label>
          <select id="cl-s" class="input" [ngModel]="clearSupplier()" (ngModelChange)="clearSupplier.set(+$event)">
            <option [ngValue]="0">Choose a supplier…</option>
            @for (s of suppliers(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }
          </select></div>
      }
      @if (clearWhat() === 'month') {
        <div class="field" style="margin-top:.7rem"><label for="cl-m">Month</label>
          <select id="cl-m" class="input" [ngModel]="clearMonth()" (ngModelChange)="clearMonth.set($event)">
            @for (m of recentMonths(); track m.key) { <option [ngValue]="m.key">{{ m.label }}</option> }
          </select></div>
      }
      @if (clearError()) { <p class="notice bad" role="alert" style="margin-top:.8rem">{{ clearError() }}</p> }
      <ng-container modal-actions><button type="button" class="btn" (click)="clearOpen.set(false)">Cancel</button>
        <button type="button" class="btn btn-danger" [disabled]="busy() || !clearReady()" (click)="clear()">{{ busy() ? 'Clearing…' : 'Clear records' }}</button></ng-container>
    </app-modal>`,
  styles: `
    .notice { margin-bottom: 1rem; }
    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 1rem; margin-bottom: 1rem; }
    .tile { padding: 1rem 1.15rem; display: flex; flex-direction: column; gap: .3rem; } .tile span { color: var(--muted); font-size: .875rem; }
    .tile strong { font-size: 1.5rem; letter-spacing: -.03em; } .tile small { color: var(--muted); } .tile.owes strong { color: var(--stamp); }
    .w-auto { width: auto; } .w-qty { width: 6rem; } .w-cost { width: 8rem; }
    .sm { font-size: .75rem; } td.actions { white-space: nowrap; } .owes { color: var(--stamp); font-weight: 600; }
    tr.on { background: var(--brand-tint); }
    .link { background: none; border: 0; padding: 0; color: var(--brand); cursor: pointer; text-align: left; }
    h3.sec { margin: 0; font-size: 1rem; }
    .sec-head { display: flex; align-items: center; gap: .8rem; margin: 1.2rem 0 .4rem; } .sec-head .btn { margin-left: auto; }
    .empty .btn { margin-top: .6rem; }
    .sum { display: flex; align-items: baseline; gap: 1rem; flex-wrap: wrap; margin-top: 1rem; padding-top: .8rem; border-top: 2px solid var(--ink); }
    .sum strong { font-size: 1.5rem; margin-left: auto; }
    .meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); gap: .8rem 1.5rem; margin: 0 0 1rem; }
    .meta dt { font: 500 .6875rem/1 var(--font-mono); text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin-bottom: .25rem; } .meta dd { margin: 0; }
    .totals { margin: 1.1rem 0 0 auto; width: min(20rem, 100%); display: grid; gap: .35rem; } .totals div { display: flex; justify-content: space-between; align-items: baseline; }
    .totals dt { color: var(--muted); } .totals dd { margin: 0; } .totals .figure { font-size: 1.6rem; color: var(--brand); }
    .owed dt, .owed dd { color: var(--stamp) !important; font-weight: 700; }
  `,
})
export class SuppliesPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);

  protected readonly filterSupplier = signal(0);
  protected readonly filterMonth = signal(0);   // yyyyMM, 0 = all
  protected readonly list = new PagedList<SupplyRow>(q => this.api2.supplies({
    ...q,
    supplierId: this.filterSupplier() || undefined,
    year: this.filterMonth() ? Math.floor(this.filterMonth() / 100) : undefined,
    month: this.filterMonth() ? this.filterMonth() % 100 : undefined,
  }));
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly busy = signal(false);
  protected asNum(v: number | string) { return asNumber(String(v)); }

  protected readonly pageOwed = computed(() => this.list.items().reduce((t, r) => t + r.outstanding, 0));
  protected readonly monthTotal = computed(() => {
    const now = new Date(); const key = now.getFullYear() * 100 + now.getMonth() + 1;
    return this.list.items().filter(r => this.monthKeyOf(r.supplyDate) === key).reduce((t, r) => t + r.totalAmount, 0);
  });
  protected readonly rowsThisMonth = computed(() => {
    const now = new Date(); const key = now.getFullYear() * 100 + now.getMonth() + 1;
    return this.list.items().filter(r => this.monthKeyOf(r.supplyDate) === key).length;
  });
  private monthKeyOf(date: string) { const d = new Date(date); return d.getFullYear() * 100 + d.getMonth() + 1; }

  /** The last 12 months, newest first, for the filter and the "clear a month" choice. */
  protected readonly recentMonths = computed(() => {
    const out: { key: number; label: string }[] = [];
    const now = new Date();
    for (let i = 0; i < 12; i++) {
      const d = new Date(now.getFullYear(), now.getMonth() - i, 1);
      out.push({ key: d.getFullYear() * 100 + d.getMonth() + 1, label: `${MONTHS[d.getMonth()]} ${d.getFullYear()}` });
    }
    return out;
  });

  ngOnInit() {
    void this.list.load();
    void this.api.suppliers({ pageSize: 200 }).then(s => this.suppliers.set(s.items), () => { /* the page still lists records */ });
  }

  protected reload() { void this.list.load(); }

  // ---- record a supply
  protected readonly formOpen = signal(false);
  protected readonly supplierId = signal(0);
  protected readonly supplyDate = signal('');
  protected readonly paidNow = signal<number | string>(0);
  protected readonly method = signal('Cash');
  protected readonly note = signal('');
  protected readonly items = signal<SupplierProduct[]>([]);
  protected readonly loadingItems = signal(false);
  // An untouched row has no entry at all, so the value really can be undefined — typed that way so the template's `??` guards stay honest.
  protected readonly qty = signal<Record<number, number | undefined>>({});
  protected readonly cost = signal<Record<number, number | undefined>>({});
  protected readonly formError = signal('');
  protected readonly itemsOpen = signal(false);

  /** After the item list is edited, pull it in again without losing the quantities already typed. */
  protected refreshItems() {
    const id = this.supplierId(); if (!id) return;
    void this.api2.supplierCatalog(id).then(xs => { if (this.supplierId() === id) this.items.set(xs); }, () => { /* keep what is on screen */ });
  }

  protected readonly supplierName = computed(() => this.suppliers().find(s => s.id === this.supplierId())?.name ?? 'This supplier');
  protected readonly chosen = computed<Picked[]>(() => {
    const q = this.qty(); const c = this.cost();
    return this.items().flatMap(i => {
      const qty = q[i.id] ?? 0;
      return qty > 0 ? [{ supplierProductId: i.id, name: i.name, unit: i.unit, qty, cost: c[i.id] ?? i.unitCost }] : [];
    });
  });
  protected readonly total = computed(() => this.chosen().reduce((t, l) => t + l.qty * l.cost, 0));
  protected readonly totalUnits = computed(() => this.chosen().reduce((t, l) => t + l.qty, 0));
  protected readonly owedAfter = computed(() => Math.max(0, this.total() - this.asNum(this.paidNow())));
  protected lineTotal(it: SupplierProduct) { const q = this.qty()[it.id] ?? 0; return q * (this.cost()[it.id] ?? it.unitCost); }

  constructor() {
    // Choosing a supplier loads the items they supply. Keyed on the id alone so typing elsewhere never reloads (or clears) the list.
    effect(() => {
      const id = this.supplierId();
      untracked(() => {
        this.items.set([]); this.qty.set({}); this.cost.set({});
        if (!id) return;
        this.loadingItems.set(true);
        void this.api2.supplierCatalog(id).then(
          xs => { if (this.supplierId() === id) this.items.set(xs); },
          () => { /* the empty state explains how to add items */ },
        ).finally(() => this.loadingItems.set(false));
      });
    });
  }

  protected openNew() {
    this.supplierId.set(this.filterSupplier() || 0);
    this.supplyDate.set(new Date().toISOString().slice(0, 10));
    this.paidNow.set(0); this.method.set('Cash'); this.note.set(''); this.formError.set('');
    this.qty.set({}); this.cost.set({});
    this.formOpen.set(true);
  }

  protected setQty(productId: number, v: string) {
    const n = Math.max(0, Math.floor(asNumber(v)));
    this.qty.update(m => ({ ...m, [productId]: n }));
  }
  protected setCost(productId: number, v: string) {
    const n = Math.max(0, asNumber(v));
    this.cost.update(m => ({ ...m, [productId]: n }));
  }

  protected async save() {
    const lines = this.chosen(); if (!lines.length || this.busy()) return;
    this.busy.set(true); this.formError.set('');
    try {
      const r = await this.api2.createSupply({
        supplierId: this.supplierId(), supplyDate: this.supplyDate() || null, paidNow: this.asNum(this.paidNow()),
        paymentMethod: this.method(), note: this.note().trim() || null,
        lines: lines.map(l => ({ supplierProductId: l.supplierProductId, quantity: l.qty, unitCost: l.cost })),
      });
      const naira = new NairaPipe();
      this.toasts.ok(`Supply ${r.reference} recorded — ${naira.transform(r.total)}${r.outstanding > 0 ? `, ${naira.transform(r.outstanding)} still owed` : ''}.`);
      this.formOpen.set(false);
      await this.list.load();
    } catch (e) { this.formError.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  // ---- one record / payment
  protected readonly viewing = signal<SupplyDetail | null>(null);
  protected readonly paying = signal<SupplyRow | null>(null);
  protected readonly payError = signal('');
  protected payAmount: number | string = 0;
  protected payMethod = 'Cash';

  protected async open(s: SupplyRow) {
    try { this.viewing.set(await this.api2.supply(s.id)); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected openPay(s: SupplyRow) { this.paying.set(s); this.payAmount = s.outstanding; this.payMethod = 'Cash'; this.payError.set(''); }

  protected async pay() {
    const s = this.paying(); if (!s) return;
    this.busy.set(true); this.payError.set('');
    try {
      const r = await this.api2.paySupply(s.id, this.asNum(this.payAmount), this.payMethod);
      const naira = new NairaPipe();
      this.toasts.ok(r.outstanding > 0 ? `Paid. ${naira.transform(r.outstanding)} still owed on ${r.reference}.` : `${r.reference} is fully paid.`);
      this.paying.set(null); await this.list.load();
    } catch (e) { this.payError.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async remove(s: SupplyRow) {
    const ok = await this.confirm.ask({
      title: `Delete ${s.reference}?`,
      message: `This removes the record that ${s.supplier} supplied ${new NairaPipe().transform(s.totalAmount)} on ${s.supplyDate}. Stock and every other record stay as they are. A copy is kept in the activity log.`,
      confirmLabel: 'Delete record', danger: true,
    });
    if (ok === null) return;
    try { await this.api2.deleteSupply(s.id); this.toasts.ok('Record deleted.'); await this.list.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  // ---- clear records (CEO only)
  protected readonly clearOpen = signal(false);
  protected readonly clearWhat = signal<'supplier' | 'month' | 'all'>('supplier');
  protected readonly clearSupplier = signal(0);
  protected readonly clearMonth = signal(0);
  protected readonly clearError = signal('');
  protected readonly clearReady = computed(() =>
    this.clearWhat() === 'all' || (this.clearWhat() === 'supplier' ? this.clearSupplier() > 0 : this.clearMonth() > 0));

  protected openClear() {
    this.clearWhat.set('supplier'); this.clearSupplier.set(0);
    this.clearMonth.set(this.recentMonths()[0]?.key ?? 0);
    this.clearError.set(''); this.clearOpen.set(true);
  }

  protected async clear() {
    const what = this.clearWhat();
    const label = what === 'all' ? 'every supply record there is'
      : what === 'supplier' ? `every supply record for ${this.suppliers().find(s => s.id === this.clearSupplier())?.name ?? 'this supplier'}`
      : `every supply record in ${this.recentMonths().find(m => m.key === this.clearMonth())?.label ?? 'this month'}`;
    const ok = await this.confirm.ask({
      title: 'Clear these records?', message: `This permanently removes ${label}. Stock, products, suppliers, sales and purchase orders are untouched. It cannot be undone.`,
      confirmLabel: 'Clear records', danger: true,
    });
    if (ok === null) return;
    this.busy.set(true); this.clearError.set('');
    try {
      const r = what === 'all' ? await this.api2.clearAllSupplies()
        : what === 'supplier' ? await this.api2.deleteSuppliesOfSupplier(this.clearSupplier())
        : await this.api2.clearSuppliesForMonth(Math.floor(this.clearMonth() / 100), this.clearMonth() % 100);
      this.toasts.ok(r.records ? `${r.records} record(s) removed (${new NairaPipe().transform(r.value)}).` : 'There was nothing to remove.');
      this.clearOpen.set(false); await this.list.load();
    } catch (e) { this.clearError.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
