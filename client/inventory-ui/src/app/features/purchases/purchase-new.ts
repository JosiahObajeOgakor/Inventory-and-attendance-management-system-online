import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { AdviceLine, SuggestedLine } from '../../core/models-dash';
import { Product, PurchaseDetail, PurchaseRequest, Supplier, Warehouse } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Toasts } from '../../shared/feedback';
import { asNumber } from '../../shared/paged-list';
import { NairaPipe } from '../../shared/ui';

interface Line { productId: number; name: string; sku: string; qty: number; cost: number; }
const VAT = 7.5;

@Component({
  selector: 'app-purchase-new',
  imports: [ReactiveFormsModule, RouterLink, Icon, NairaPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>{{ edit ? 'Edit purchase ' + (original()?.poNumber ?? '') : 'New purchase' }}</h1>
        <div class="actions"><a class="btn" [routerLink]="edit ? ['/purchases', editId] : '/purchases'">Cancel</a></div></div>
      @if (edit && original(); as o) {
        <p class="notice info">
          @if (o.status === 'Received') { These goods are already in stock. Only the difference moves: extra units are added to this order’s batch, and units you take off leave it — as long as they haven’t been sold yet. }
          @else { This order hasn’t been received yet, so no stock moves — only the order and what you owe the supplier change. }
        </p>
      }

      <form [formGroup]="form" (ngSubmit)="save()" class="layout" novalidate>
        <div class="left">
          <section class="card card-pad form-grid">
            <div class="field"><label for="sup">Supplier</label>
              <select id="sup" class="input" formControlName="supplierId"><option [ngValue]="0" disabled>Choose a supplier</option>@for (s of suppliers(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }</select>
              @if (supplier(); as s) { @if (s.balance > 0 && !edit) { <span class="hint owes">We already owe them {{ s.balance | naira }}</span> } @if (s.balance < 0) { <span class="hint credit">We have {{ -s.balance | naira }} credit with them</span> } }</div>
            @if (!edit) {
              <div class="field"><label>&nbsp;</label><button type="button" class="btn" [disabled]="!supplier() || suggesting()" (click)="suggest()"><app-icon name="spark" [size]="16" /> {{ suggesting() ? 'Working it out…' : 'Suggest what to order' }}</button></div>
              <div class="field"><label for="od">Order date</label><input id="od" class="input" type="date" formControlName="orderDate" /></div>
            }
          </section>

          @if (suggested().length) {
            <section class="card">
              <div class="toolbar"><strong class="grow">Suggested from your sales forecast</strong><button type="button" class="btn btn-sm btn-primary" (click)="addAllSuggested()">Add all</button></div>
              <div class="table-wrap"><table class="table">
                <thead><tr><th>Product</th><th class="num">Suggest</th><th class="num">On hand</th><th>Runs out</th><th></th></tr></thead>
                <tbody>@for (s of suggested(); track s.productId) {
                  <tr><td><span class="strong">{{ s.product }}</span><div class="muted sm">{{ s.basis }}</div></td><td class="num mono">{{ s.quantity }} {{ s.unit }}</td><td class="num mono">{{ s.onHand }}</td>
                    <td>{{ s.runsOutOn ?? '—' }}</td><td class="actions"><button type="button" class="btn btn-sm" (click)="addSuggested(s)">Add</button></td></tr>
                }</tbody>
              </table></div>
            </section>
          }


          <section class="card">
            <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
              <input #q class="input" placeholder="Search products to add" aria-label="Add a product" (input)="search(q.value)" autocomplete="off" /></div></div>
            @if (results().length) {
              <ul class="pick">@for (p of results(); track p.id) { <li><button type="button" (click)="add(p); q.value = ''; results.set([])"><span><strong>{{ p.name }}</strong> <span class="muted mono">{{ p.sku }}</span></span><span class="mono muted">last cost {{ p.costPrice | naira }}</span></button></li> }</ul>
            }
            @if (lines().length) {
              <div class="table-wrap lines-wrap"><table class="table lines">
                <thead><tr><th>Product</th><th class="num">Qty</th><th class="num">Unit cost (₦)</th><th class="num">Line total</th><th><span class="sr-only">Remove</span></th></tr></thead>
                <tbody>@for (l of lines(); track l.productId; let i = $index) {
                  <tr><td class="prod"><span class="strong">{{ l.name }}</span><div class="muted mono sm">{{ l.sku }}</div></td>
                    <td class="num qty" data-label="Qty"><input class="input num w-qty" type="number" min="1" step="1" inputmode="numeric" [value]="l.qty" [attr.aria-label]="'Quantity of ' + l.name" (input)="patch(i, { qty: whole($any($event.target).value) })" /></td>
                    <td class="num price" data-label="Unit cost (₦)"><input class="input num w-price" type="number" min="0" step="0.01" inputmode="decimal" [value]="l.cost" [attr.aria-label]="'Unit cost of ' + l.name" (input)="patch(i, { cost: money($any($event.target).value) })" /></td>
                    <td class="num mono lt" data-label="Line total">{{ l.qty * l.cost | naira }}</td>
                    <td class="actions rm"><button type="button" class="btn btn-quiet btn-icon" (click)="remove(i)" [attr.aria-label]="'Remove ' + l.name"><app-icon name="close" [size]="18" /></button></td></tr>
                }</tbody>
              </table></div>
            } @else { <div class="empty"><strong>No items yet</strong>Search above to add what you’re buying.</div> }
          </section>
          @if (advice().length) { <div class="notice info" role="status"><div><strong>Worth a look</strong><ul>@for (a of advice(); track a.text) { <li>{{ a.text }}</li> }</ul></div></div> }
          @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
        </div>

        <aside class="summary card card-pad">
          <label class="check"><input type="checkbox" formControlName="vat" /> Add VAT ({{ vatRate }}%)</label>
          <dl class="totals">
            <div><dt>Subtotal</dt><dd class="mono">{{ subtotal() | naira }}</dd></div>
            @if (vatAmount() > 0) { <div><dt>VAT</dt><dd class="mono">{{ vatAmount() | naira }}</dd></div> }
            @if (carriesBalance()) {
              <!-- What we already owe this supplier (or credit we hold with them) is brought into this bill. -->
              <div class="sale-total"><dt>This order</dt><dd class="mono strong">{{ total() | naira }}</dd></div>
              @if (supplier()!.balance > 0) {
                <div class="bf"><dt>Previous balance (we owe)</dt><dd class="mono">+{{ supplier()!.balance | naira }}</dd></div>
              } @else {
                <div class="cr"><dt>Credit with this supplier</dt><dd class="mono">−{{ -supplier()!.balance | naira }}</dd></div>
              }
              <div class="grand"><dt>Total due now</dt><dd class="figure">{{ dueNow() | naira }}</dd></div>
            } @else {
              <div class="grand"><dt>Total</dt><dd class="figure">{{ total() | naira }}</dd></div>
            }
          </dl>
          @if (edit && original(); as o) {
            <div class="changes" aria-live="polite">
              <div class="label">What this edit does</div>
              <ul class="plain">
                @if (o.status === 'Received') {
                  @for (m of stockMoves(); track m.productId) {
                    <li>{{ m.qty > 0 ? 'Adds' : 'Takes' }} <strong>{{ m.qty > 0 ? m.qty : -m.qty }} × {{ m.name }}</strong> {{ m.qty > 0 ? 'to' : 'out of' }} stock</li>
                  } @empty { <li class="muted">No stock moves.</li> }
                }
                <li>Total {{ o.totalAmount | naira }} → <strong>{{ total() | naira }}</strong>
                  @if (total() > o.totalAmount) { — you owe the supplier {{ total() - o.totalAmount | naira }} more. }
                  @if (total() < o.totalAmount) { — you owe the supplier {{ o.totalAmount - total() | naira }} less. }</li>
                @if (o.amountPaid > total()) {
                  <li>{{ o.amountPaid - total() | naira }} already paid is more than the new total. It pays your other unpaid orders with them first; anything left is credit for your next order.</li>
                }
              </ul>
            </div>
          } @else {
          <label class="check"><input type="checkbox" formControlName="receiveNow" /> The goods are already here — add them to stock</label>
          @if (form.controls.receiveNow.value) {
            <div class="field"><label for="wh">Put them in</label><select id="wh" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
          }
          <div class="field"><label for="paid">Paid to the supplier now</label>
            <div class="inline"><input id="paid" class="input num" type="number" min="0" step="0.01" formControlName="paidNow" />
              <button type="button" class="btn btn-sm" [disabled]="!lines().length" (click)="payAll()">{{ (supplier()?.balance ?? 0) > 0 ? 'Pay all' : 'Full' }}</button></div>
            <span class="hint">Covers this order first, then older orders, oldest first.</span>
            @if (carriesBalance()) { <span class="hint owes">After this payment we will owe them {{ owedAfter() | naira }} in total.</span> }</div>
          <div class="field"><label for="pm">Paid by</label><select id="pm" class="input" formControlName="paymentMethod"><option>Cash</option><option>Bank Transfer</option><option>Card</option></select></div>
          }
          <button class="btn btn-primary big" type="submit" [disabled]="busy() || !request()">{{ busy() ? 'Saving…' : edit ? 'Save changes' : 'Save purchase' }}</button>
        </aside>
      </form>
    </div>`,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 21rem; gap: 1rem; align-items: start; } .left { display: flex; flex-direction: column; gap: 1rem; min-width: 0; }
    .summary { position: sticky; top: 4.5rem; display: flex; flex-direction: column; gap: 1rem; }
    .pick { list-style: none; margin: 0; padding: 0; border-bottom: 1px solid var(--line); max-height: 15rem; overflow: auto; background: #fff; }
    .pick button { display: flex; justify-content: space-between; gap: 1rem; width: 100%; padding: .6rem 1.1rem; background: none; border: 0; border-bottom: 1px solid var(--line); text-align: left; cursor: pointer; }
    .pick button:hover, .pick button:focus-visible { background: var(--brand-tint); }
    .sm { font-size: .75rem; } .owes { color: var(--stamp) !important; font-weight: 600; } .credit { color: var(--ok, #1a7f4b) !important; font-weight: 600; }
    .inline { display: flex; gap: .4rem; }
    .lines-wrap { container-type: inline-size; }
    @container (max-width: 560px) {
      .lines thead { display: none; }
      .lines tr { display: grid; grid-template-columns: 1fr 1fr auto; gap: .55rem .75rem; padding: .85rem 1rem; border-bottom: 1px solid var(--line); }
      .lines td { display: block; padding: 0; border: 0; text-align: left; }
      .lines td.prod { grid-column: 1 / 3; grid-row: 1; }
      .lines td.rm { grid-column: 3; grid-row: 1; align-self: start; }
      .lines td.qty { grid-column: 1; grid-row: 2; }
      .lines td.price { grid-column: 2 / 4; grid-row: 2; }
      .lines td.lt { grid-column: 1 / -1; grid-row: 3; text-align: right; font-weight: 700; }
      .lines td[data-label]::before { content: attr(data-label); display: block; font: 600 .6875rem/1.2 var(--font-body); color: var(--muted); margin-bottom: .25rem; }
      .lines td.lt::before { display: inline; margin-right: .5rem; }
      .lines .w-qty, .lines .w-price { width: 100%; }
    }
    .totals .sale-total { border-top: 1px solid var(--line); padding-top: .4rem; }
    .totals .bf dt, .totals .bf dd { color: var(--stamp); font-weight: 600; }
    .totals .cr dt, .totals .cr dd { color: var(--ok, #1a7f4b); font-weight: 600; }
    .label { font: 600 .75rem/1 var(--font-body); letter-spacing: .04em; color: var(--ink-3); margin-bottom: .55rem; }
    .changes { border-top: 1px solid var(--line); padding-top: .75rem; font-size: .8125rem; } .plain { list-style: none; margin: 0; padding: 0; display: grid; gap: .35rem; } .w-qty { width: 5rem; } .w-price { width: 8rem; } .big { min-height: 3rem; }
    .totals { margin: 0; display: grid; gap: .35rem; } .totals div { display: flex; justify-content: space-between; } .totals dt { color: var(--muted); } .totals dd { margin: 0; }
    .grand { border-top: 2px solid var(--ink); padding-top: .5rem; align-items: baseline; } .grand dt { color: var(--ink) !important; font-weight: 700; } .grand dd { font-size: 2rem; color: var(--brand); }
    @media (max-width: 1100px) { .layout { grid-template-columns: minmax(0, 1fr); } .summary { position: static; } }
  `,
})
export class PurchaseNew implements OnInit {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);
  protected readonly edit = this.route.snapshot.data['mode'] === 'edit';
  protected readonly editId = Number(this.route.snapshot.paramMap.get('id')) || 0;
  protected readonly original = signal<PurchaseDetail | null>(null);
  protected readonly vatRate = VAT;

  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly lines = signal<Line[]>([]);
  protected readonly results = signal<Product[]>([]);
  protected readonly busy = signal(false);
  protected readonly suggested = signal<SuggestedLine[]>([]);
  protected readonly advice = signal<AdviceLine[]>([]);
  protected readonly suggesting = signal(false);
  private adviceTimer: ReturnType<typeof setTimeout> | null = null;
  // Advice on the order: a missing partner product, or a unit cost far from what this supplier has charged before.
  private readonly adviceFx = effect(() => {
    const s = this.supplier(); const ls = this.lines();
    if (this.adviceTimer) clearTimeout(this.adviceTimer);
    if (!s || !ls.length) { this.advice.set([]); return; }
    this.adviceTimer = setTimeout(async () => {
      try { this.advice.set(await this.api2.purchaseAdvice(s.id, ls.map(l => ({ productId: l.productId, unitCost: l.cost })))); } catch { this.advice.set([]); }
    }, 700);
  });
  protected readonly error = signal('');

  protected readonly form = this.fb.nonNullable.group({
    supplierId: [0, Validators.min(1)], orderDate: [''], vat: [false], receiveNow: [false], warehouseId: [0], paidNow: [0, Validators.min(0)], paymentMethod: ['Cash'],
  });
  // Raw value: in edit mode the supplier is locked (disabled) but still part of the order.
  private readonly value = toSignal(this.form.valueChanges.pipe(map(() => this.form.getRawValue())), { initialValue: this.form.getRawValue() });
  protected readonly supplier = computed(() => this.suppliers().find(s => s.id === Number(this.value().supplierId)));
  protected readonly subtotal = computed(() => this.lines().reduce((s, l) => s + l.qty * l.cost, 0));
  protected readonly vatAmount = computed(() => (this.value().vat ? Math.round(this.subtotal() * VAT) / 100 : 0));
  protected readonly total = computed(() => this.subtotal() + this.vatAmount());
  /** A new order to a supplier we already owe (or hold credit with): the bill shows the previous balance and one "total due now". */
  protected readonly carriesBalance = computed(() => !this.edit && (this.supplier()?.balance ?? 0) !== 0);
  protected readonly dueNow = computed(() => Math.max(0, this.total() + (this.supplier()?.balance ?? 0)));
  protected readonly owedAfter = computed(() => Math.max(0, this.dueNow() - Math.max(0, asNumber(this.value().paidNow))));
  protected payAll() { this.form.controls.paidNow.setValue(this.dueNow()); }
  /** Edit mode: per product, how many more (+) or fewer (−) than the saved order. */
  protected readonly stockMoves = computed(() => {
    const o = this.original(); if (!o) return [];
    const was = new Map<number, { name: string; qty: number }>();
    for (const i of o.items) was.set(i.productId, { name: i.product, qty: (was.get(i.productId)?.qty ?? 0) + i.quantity });
    const now = new Map<number, { name: string; qty: number }>();
    for (const l of this.lines()) now.set(l.productId, { name: l.name, qty: (now.get(l.productId)?.qty ?? 0) + l.qty });
    return [...new Set([...was.keys(), ...now.keys()])]
      .map(id => ({ productId: id, name: (now.get(id) ?? was.get(id))!.name, qty: (now.get(id)?.qty ?? 0) - (was.get(id)?.qty ?? 0) }))
      .filter(m => m.qty !== 0);
  });
  protected readonly request = computed<PurchaseRequest | null>(() => {
    const v = this.value(); const lines = this.lines();
    if (!(Number(v.supplierId) > 0) || !lines.length || lines.some(l => !(l.qty > 0))) return null;
    return {
      supplierId: Number(v.supplierId), orderDate: v.orderDate || null, vatRate: v.vat ? VAT : 0, receiveNow: !!v.receiveNow, warehouseId: Number(v.warehouseId) || 0,
      paidNow: asNumber(v.paidNow), paymentMethod: v.paymentMethod ?? 'Cash', lines: lines.map(l => ({ productId: l.productId, quantity: l.qty, unitCost: l.cost })),
    };
  });

  private timer: ReturnType<typeof setTimeout> | null = null;
  private idem: { body: string; id: string } | null = null;

  async ngOnInit() {
    try {
      const [s, w] = await Promise.all([this.api.suppliers({ pageSize: 200 }), this.api.warehouses()]);
      this.suppliers.set(s.items); this.warehouses.set(w);
      if (w[0]) this.form.controls.warehouseId.setValue(w[0].id);
      if (this.edit) {
        const o = await this.api.purchase(this.editId);
        if (o.status === 'Cancelled') { this.error.set('This order was cancelled and can no longer be edited.'); return; }
        // The order doesn't store its VAT rate; it had VAT if the total is more than its lines.
        const sub = o.items.reduce((t, i) => t + i.lineTotal, 0);
        this.form.patchValue({ supplierId: o.supplierId, vat: o.totalAmount > sub + 0.005 });
        this.form.controls.supplierId.disable();
        this.lines.set(o.items.map(i => ({ productId: i.productId, name: i.product, sku: i.sku, qty: i.quantity, cost: i.unitCost })));
        this.original.set(o);
      }
    } catch (e) { this.error.set(messageOf(e)); }
  }

  protected whole(v: string) { return Math.max(0, Math.floor(asNumber(v))); }
  protected money(v: string) { return Math.max(0, asNumber(v)); }

  protected search(term: string) {
    if (this.timer) clearTimeout(this.timer);
    const t = term.trim();
    if (t.length < 2) { this.results.set([]); return; }
    this.timer = setTimeout(async () => { try { this.results.set((await this.api.products({ search: t, pageSize: 8 })).items); } catch { this.results.set([]); } }, 220);
  }

  /** What this supplier has sent before that won't last the lead time, sized from the demand forecast. */
  protected async suggest() {
    const s = this.supplier(); if (!s) return;
    this.suggesting.set(true);
    try { this.suggested.set(await this.api2.suggestedOrder(s.id)); if (!this.suggested().length) this.toasts.info('Nothing needs ordering from this supplier right now.'); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.suggesting.set(false); }
  }
  protected addSuggested(s: SuggestedLine) {
    this.lines.update(ls => ls.some(l => l.productId === s.productId) ? ls : [...ls, { productId: s.productId, name: s.product, sku: '', qty: s.quantity, cost: s.unitCost }]);
    this.suggested.update(x => x.filter(y => y.productId !== s.productId));
  }
  protected addAllSuggested() { for (const s of [...this.suggested()]) this.addSuggested(s); }


  protected add(p: Product) {
    this.lines.update(ls => ls.some(l => l.productId === p.id)
      ? ls.map(l => (l.productId === p.id ? { ...l, qty: l.qty + 1 } : l))
      : [...ls, { productId: p.id, name: p.name, sku: p.sku, qty: 1, cost: p.costPrice ?? 0 }]);
  }
  protected patch(i: number, p: Partial<Line>) { this.lines.update(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l))); }
  protected remove(i: number) { this.lines.update(ls => ls.filter((_, j) => j !== i)); }

  protected async save() {
    const r = this.request(); if (!r || this.busy()) return;
    this.busy.set(true); this.error.set('');
    try {
      if (this.edit) {
        const res = await this.api.editPurchase(this.editId, { vatRate: r.vatRate, lines: r.lines });
        const extra = res.creditHeld > 0 ? ` You now have ${new NairaPipe().transform(res.creditHeld)} credit with this supplier.` : '';
        this.toasts.ok(`Purchase ${res.poNumber} updated.${extra}`);
        await this.router.navigate(['/purchases', res.purchaseOrderId]);
        return;
      }
      const body = JSON.stringify(r);
      if (!this.idem || this.idem.body !== body) this.idem = { body, id: crypto.randomUUID() };
      const res = await this.api.createPurchase(r, this.idem.id);
      this.toasts.ok(`Purchase ${res.poNumber} saved.`);
      await this.router.navigate(['/purchases', res.purchaseOrderId]);
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
