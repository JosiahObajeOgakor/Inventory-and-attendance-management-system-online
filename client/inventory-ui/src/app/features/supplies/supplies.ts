import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Auth } from '../../core/auth.service';
import { Product, Supplier, Warehouse } from '../../core/models';
import { SupplierProduct, SupplyDetail, SupplyRow } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { PagedList, asNumber } from '../../shared/paged-list';
import { DayPipe, NairaPipe, Pager, Stamp } from '../../shared/ui';
import { SupplierCatalog } from '../suppliers/supplier-catalog';
import { SendDocument } from '../../shared/send-document';

interface Picked { supplierProductId: number; name: string; unit: string; qty: number; cost: number; }

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

/**
 * Buying from suppliers. Choosing a supplier lists THAT supplier's own items; you type the quantity and price against each,
 * then save. Nothing here touches stock, a product's cost price or the ledger, so a supplier's figures stand on their own.
 */
@Component({
  selector: 'app-supplies',
  imports: [FormsModule, RouterLink, Icon, Modal, SupplierCatalog, SendDocument, NairaPipe, DayPipe, Pager, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Purchases</h1>
          <p class="crumb">{{ auth.companyName() }} <span>›</span> What we buy from suppliers</p>
        </div>
        <div class="actions">
          <button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> Record a purchase</button>
          @if (auth.canDelete()) { <button type="button" class="btn btn-danger" (click)="openClear()">Clear records</button> }
        </div>
      </div>

      <p class="notice">Buying from a supplier means picking from that supplier’s own item list. It records what you bought, what you paid and what you still owe — it never changes stock
        levels or any product’s cost price. For goods that must go onto your shelves, use Stock purchases instead.</p>

      <section class="tiles">
        <article class="card tile"><span>Bought this month</span><strong class="mono">{{ monthTotal() | naira }}</strong>
          <small>{{ rowsThisMonth() }} record(s) on this page</small></article>
        <article class="card tile" [class.owes]="pageOwed() > 0"><span>Owed on these records</span><strong class="mono">{{ pageOwed() | naira }}</strong>
          <small>Across the records listed below</small></article>
      </section>

      <section class="card">
        <div class="toolbar">
          <div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search reference or supplier" aria-label="Search purchases" (input)="list.setSearch($any($event.target).value)" /></div>
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
                  <button type="button" class="btn btn-sm" (click)="sending.set(s)"><app-icon name="send" [size]="15" /> Send</button>
                  <a class="btn btn-sm" [href]="'/api/supplies/' + s.id + '/pdf'" target="_blank" rel="noopener"><app-icon name="print" [size]="15" /> PDF</a>
                  @if (s.outstanding > 0) { <button type="button" class="btn btn-sm" (click)="openPay(s)">Pay</button> }
                  @if (auth.canDelete()) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(s)">Delete</button> }
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No purchases yet</strong>Use “Record a purchase” after a supplier delivers.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <!-- record a purchase -->
    <app-modal [open]="formOpen()" heading="Record a purchase" [wide]="true" (closed)="formOpen.set(false)">
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

        <!-- optional: the goods are here, so put them on the shelves too -->
        <fieldset class="stock-box">
          <label class="tick"><input type="checkbox" [checked]="addToStock()" (change)="addToStock.set($any($event.target).checked)" />
            <span><strong>The goods are already here — add them to stock</strong>
              <span class="muted sm">Leave this off to record the purchase only. Tick it and the quantities also go onto your shelves.</span></span></label>

          @if (addToStock()) {
            <div class="field" style="margin-top:.7rem"><label for="sv-w">Put the goods in</label>
              <select id="sv-w" class="input" [ngModel]="warehouseId()" (ngModelChange)="warehouseId.set(+$event)">
                @for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }
              </select></div>

            @if (chosen().length) {
              <p class="muted sm" style="margin:.8rem 0 .3rem">Tell us which of your products each one counts as. It is remembered, so you only choose once.</p>
              <div class="table-wrap"><table class="table">
                <thead><tr><th>Their item</th><th>Counts as which product</th></tr></thead>
                <tbody>@for (l of chosen(); track l.supplierProductId) {
                  <tr><td><span class="strong">{{ l.name }}</span><div class="muted sm">{{ l.qty }} {{ l.unit }}</div></td>
                    <td><select class="input" [ngModel]="productFor()[l.supplierProductId] ?? 0" (ngModelChange)="setProduct(l.supplierProductId, +$event)">
                      <option [ngValue]="0">Choose a product…</option>
                      @for (p of products(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
                    </select></td></tr>
                }</tbody>
              </table></div>
              @if (unmapped().length) { <p class="notice bad" style="margin-top:.6rem">Choose a product for: {{ unmapped().join(', ') }}.</p> }
            } @else { <p class="muted sm" style="margin-top:.6rem">Add quantities above first.</p> }
          }
        </fieldset>

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
        <button type="button" class="btn btn-primary" [disabled]="busy() || !chosen().length || unmapped().length > 0" (click)="save()">{{ busy() ? 'Saving…' : addToStock() ? 'Save and add to stock' : 'Save purchase' }}</button>
      </ng-container>
    </app-modal>

    <!-- one record -->
    <app-modal [open]="!!viewing()" [heading]="'Purchase ' + (viewing()?.reference ?? '')" [wide]="true" (closed)="viewing.set(null)">
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
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="viewing.set(null)">Close</button>
        @if (viewing(); as v) {
          <a class="btn" [href]="'/api/supplies/' + v.id + '/pdf'" target="_blank" rel="noopener"><app-icon name="print" [size]="17" /> Download PDF</a>
          <button type="button" class="btn btn-primary" (click)="sendFromView(v)"><app-icon name="send" [size]="17" /> Send to supplier</button>
        }
      </ng-container>
    </app-modal>

    <app-send-document [open]="!!sending()" kind="supplies" [docId]="sending()?.id ?? null" [number]="sending()?.reference ?? ''"
      [defaultPhone]="sendPhone()" [defaultEmail]="''" (closed)="sending.set(null)" />

    <!-- pay one record -->
    <app-modal [open]="!!paying()" [heading]="'Pay ' + (paying()?.reference ?? '')" (closed)="paying.set(null)">
      <p>Still owed on this purchase: <strong class="mono">{{ paying()?.outstanding ?? 0 | naira }}</strong></p>
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
    <app-modal [open]="clearOpen()" heading="Clear purchase records" (closed)="clearOpen.set(false)">
      <p>This removes these purchase records only. Stock, products, suppliers, sales and purchase orders are untouched.</p>
      <div class="field" style="margin-top:.9rem"><label for="cl-w">What to clear</label>
        <select id="cl-w" class="input" [ngModel]="clearWhat()" (ngModelChange)="clearWhat.set($event)">
          <option value="supplier">Every purchase from one supplier</option>
          <option value="month">Every purchase in one month</option>
          <option value="all">Every purchase record there is</option>
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
    .stock-box { border: 1px dashed var(--line-strong); border-radius: var(--r-2); padding: .9rem 1rem; margin-top: 1rem; }
    .tick { display: flex; align-items: flex-start; gap: .6rem; cursor: pointer; } .tick span { display: flex; flex-direction: column; gap: .15rem; }
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

  /** "?supplier=12" (from a supplier's page): start with them chosen and the form already open. */
  readonly preSupplier = input<string | undefined>(undefined, { alias: 'supplier' });
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
    void this.api.suppliers({ pageSize: 200 }).then(s => {
      this.suppliers.set(s.items);
      const pre = Number(this.preSupplier());
      if (pre > 0 && s.items.some(x => x.id === pre)) { this.filterSupplier.set(pre); this.openNew(); void this.list.load(); }
    }, () => { /* the page still lists records */ });
    // Only needed for the "add to stock" half of the form, so a failure here never blocks recording a purchase.
    void this.api.warehouses().then(w => { this.warehouses.set(w); if (!this.warehouseId()) this.warehouseId.set(w[0]?.id ?? 0); }, () => { /* optional */ });
    void this.api.products({ pageSize: 500 }).then(r => this.products.set(r.items), () => { /* optional */ });
  }

  protected reload() { void this.list.load(); }

  // ---- record a purchase
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

  // ---- "the goods are already here": also put them on the shelves
  protected readonly addToStock = signal(false);
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly warehouseId = signal(0);
  protected readonly products = signal<Product[]>([]);
  /** Which product each of the supplier's items counts as. Seeded from what the item already remembers. */
  protected readonly productFor = signal<Record<number, number | undefined>>({});
  protected setProduct(supplierProductId: number, productId: number) {
    this.productFor.update(m => ({ ...m, [supplierProductId]: productId || undefined }));
  }
  /** Picked items with no product chosen yet — saving to stock is blocked until this is empty. */
  protected readonly unmapped = computed(() =>
    !this.addToStock() ? [] : this.chosen().filter(l => !this.productFor()[l.supplierProductId]).map(l => l.name));

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
          xs => {
            if (this.supplierId() !== id) return;
            this.items.set(xs);
            // Each item may already remember which product it counts as; carry that in so the choice is pre-made.
            this.productFor.set(Object.fromEntries(xs.filter(x => x.productId).map(x => [x.id, x.productId!])));
          },
          () => { /* the empty state explains how to add items */ },
        ).finally(() => this.loadingItems.set(false));
      });
    });
  }

  protected openNew() {
    this.supplierId.set(this.filterSupplier() || 0);
    this.supplyDate.set(new Date().toISOString().slice(0, 10));
    this.paidNow.set(0); this.method.set('Cash'); this.note.set(''); this.formError.set(''); this.addToStock.set(false);
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
        addToStock: this.addToStock(), warehouseId: this.addToStock() ? this.warehouseId() : 0,
        lines: lines.map(l => ({ supplierProductId: l.supplierProductId, quantity: l.qty, unitCost: l.cost,
          productId: this.addToStock() ? this.productFor()[l.supplierProductId] ?? null : null })),
      });
      const naira = new NairaPipe();
      this.toasts.ok(`Purchase ${r.reference} recorded — ${naira.transform(r.total)}${r.outstanding > 0 ? `, ${naira.transform(r.outstanding)} still owed` : ''}.`);
      this.formOpen.set(false);
      await this.list.load();
      // Straight on to sending the supplier their copy, the way a sale offers the receipt.
      this.sendPhone.set('');
      this.sending.set(this.list.items().find(x => x.id === r.id) ?? null);
    } catch (e) { this.formError.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  // ---- one record / payment
  protected readonly viewing = signal<SupplyDetail | null>(null);
  protected readonly sending = signal<SupplyRow | null>(null);
  /** The supplier's number, when we happen to know it from the record just opened; the dialog asks otherwise. */
  protected readonly sendPhone = signal('');
  protected readonly paying = signal<SupplyRow | null>(null);

  /** Send straight from the open record: it already carries the supplier's phone number. */
  protected sendFromView(v: SupplyDetail) {
    this.sendPhone.set(v.supplierPhone ?? '');
    this.viewing.set(null);
    this.sending.set(this.list.items().find(r => r.id === v.id) ?? null);
  }
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
    const label = what === 'all' ? 'every purchase record there is'
      : what === 'supplier' ? `every purchase from ${this.suppliers().find(s => s.id === this.clearSupplier())?.name ?? 'this supplier'}`
      : `every purchase record in ${this.recentMonths().find(m => m.key === this.clearMonth())?.label ?? 'this month'}`;
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
