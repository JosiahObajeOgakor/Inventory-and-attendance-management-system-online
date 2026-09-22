import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, messageOf, problemOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Auth } from '../../core/auth.service';
import { CustomerLookup, CustomerType, Problem, Product, SalePreview, SaleRequest, Warehouse } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { asNumber } from '../../shared/paged-list';
import { NairaPipe } from '../../shared/ui';

interface Line {
  productId: number; name: string; sku: string; unit: string; qty: number; price: number; standard: number; stock: number; tracks: boolean; serials: string;
  tiers: { Distributor: number; Wholesaler: number; Retailer: number };
}

type Tier = 'Distributor' | 'Wholesaler' | 'Retailer';
/** Customer type → price tier (PriceLists.TierFor in the desktop app). Walk-in pays retail. */
const tierFor = (t: CustomerType): Tier => (t === 'Distributor' ? 'Distributor' : t === 'Wholesaler' ? 'Wholesaler' : 'Retailer');
const VAT = 7.5;
const splitSerials = (t: string) => t.split(/[\r\n,;\t]+/).map(x => x.trim()).filter(Boolean);

@Component({
  selector: 'app-sale-new',
  imports: [ReactiveFormsModule, RouterLink, Icon, Modal, NairaPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>{{ quote ? 'New quotation' : 'New sale' }}</h1>
        <div class="actions"><a class="btn" [routerLink]="quote ? '/quotations' : '/sales'">Cancel</a></div>
      </div>

      <form [formGroup]="form" (ngSubmit)="save()" class="layout" novalidate>
        <div class="left">
          <!-- customer -->
          <section class="card card-pad">
            <div class="label">Customer</div>
            @if (customer(); as c) {
              <div class="chip">
                <div><strong>{{ c.name }}</strong> <span class="stamp stamp-info">{{ c.customerType }}</span>
                  @if (c.balance > 0) { <div class="owes">Owes {{ c.balance | naira }} from earlier sales</div> }</div>
                <button type="button" class="btn btn-sm" (click)="changeCustomer()">Change</button>
              </div>
            } @else {
              <div class="search"><app-icon name="search" [size]="17" />
                <input class="input" placeholder="Search customers by name or phone" aria-label="Search customers" (input)="searchCustomers($any($event.target).value)" /></div>
              <ul class="pick">
                @for (c of customers(); track c.id) {
                  <li><button type="button" (click)="chooseCustomer(c)"><span>{{ c.name }}</span><span class="muted">{{ c.customerType }}{{ c.phone ? ' · ' + c.phone : '' }}</span></button></li>
                }
              </ul>
              <button type="button" class="btn btn-sm" (click)="newCustomerOpen.set(true)"><app-icon name="plus" [size]="16" /> New customer</button>
            }
          </section>

          <!-- products -->
          <section class="card">
            <div class="toolbar">
              <div class="search grow"><app-icon name="scan" [size]="17" />
                <input #q class="input" placeholder="Scan a barcode or search products" aria-label="Add a product"
                       (input)="searchProducts(q.value)" (keydown.enter)="addFromEnter(q); $event.preventDefault()" autocomplete="off" /></div>
            </div>
            @if (results().length) {
              <ul class="pick results">
                @for (p of results(); track p.id) {
                  <li><button type="button" (click)="add(p); q.value = ''; results.set([])">
                    <span><strong>{{ p.name }}</strong> <span class="muted mono">{{ p.sku }}</span></span>
                    <span class="mono">{{ priceOf(p) | naira }} · <span [class.low]="p.totalQuantity <= p.reorderLevel">{{ p.totalQuantity }} in stock</span></span>
                  </button></li>
                }
              </ul>
            }

            @if (lines().length) {
              <div class="table-wrap"><table class="table">
                <thead><tr><th>Product</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Line total</th><th><span class="sr-only">Remove</span></th></tr></thead>
                <tbody>
                  @for (l of lines(); track l.productId; let i = $index) {
                    <tr>
                      <td><span class="strong">{{ l.name }}</span><div class="muted mono sm">{{ l.sku }} · {{ l.stock }} {{ l.unit }} in stock</div>
                        @if (l.qty > l.stock && !quote) { <div class="short">Only {{ l.stock }} in stock</div> }
                        @if (l.tracks && !quote) {
                          <label class="serial"><span>Serial numbers — one per line ({{ serialCount(l) }} of {{ l.qty }})</span>
                            <textarea class="input" rows="2" [value]="l.serials" (input)="setSerials(i, $any($event.target).value)" [attr.aria-label]="'Serial numbers for ' + l.name"></textarea></label>
                        }</td>
                      <td class="num"><input class="input num w-qty" type="number" min="1" step="1" [value]="l.qty" [attr.aria-label]="'Quantity of ' + l.name" (input)="setQty(i, $any($event.target).value)" /></td>
                      <td class="num"><input class="input num w-price" type="number" min="0" step="0.01" [value]="l.price" [attr.aria-label]="'Price of ' + l.name" (input)="setPrice(i, $any($event.target).value)" />
                        @if (l.price !== l.standard) { <div class="ovr">Changed from {{ l.standard | naira }}</div> }</td>
                      <td class="num mono">{{ l.qty * l.price | naira }}</td>
                      <td class="actions"><button type="button" class="btn btn-quiet btn-icon" (click)="remove(i)" [attr.aria-label]="'Remove ' + l.name"><app-icon name="close" [size]="18" /></button></td>
                    </tr>
                  }
                </tbody>
              </table></div>
            } @else { <div class="empty"><strong>No products yet</strong>Scan a barcode or search above to add the first line.</div> }
          </section>

          @if (advice().length) {
            <div class="notice info advice" role="status"><div><strong>Worth a look</strong>
              <ul>@for (a of advice(); track a.text) { <li>{{ a.text }}</li> }</ul></div></div>
          }
          @if (shortfalls().length) {
            <div class="notice bad" role="alert"><div>
              <strong>Not enough stock to save this sale.</strong>
              <ul>@for (s of shortfalls(); track s.productId) { <li>{{ s.productName }} — asked for {{ s.requested }}, only {{ s.available }} available (short by {{ s.shortBy }}). {{ s.advice }}</li> }</ul>
            </div></div>
          }
          @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
        </div>

        <!-- summary -->
        <aside class="summary card card-pad">
          <div class="form-grid one">
            @if (!quote) { <div class="field"><label for="wh">Take stock from</label>
              <select id="wh" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div> }
            <div class="field"><label for="tier">Price list</label>
              <select id="tier" class="input" formControlName="priceTier"><option>Retailer</option><option>Wholesaler</option><option>Distributor</option></select></div>
            <div class="field"><label for="disc">Discount %</label><input id="disc" class="input num" type="number" min="0" max="100" step="0.5" formControlName="discountPct" /></div>
            <label class="check"><input type="checkbox" formControlName="vat" /> Charge VAT ({{ vatRate }}%)</label>
          </div>

          <dl class="totals">
            <div><dt>Subtotal</dt><dd class="mono">{{ (preview()?.subtotal ?? localSubtotal()) | naira }}</dd></div>
            @if ((preview()?.discountAmount ?? 0) > 0) { <div><dt>Discount</dt><dd class="mono">−{{ preview()!.discountAmount | naira }}</dd></div> }
            @if ((preview()?.vatAmount ?? 0) > 0) { <div><dt>VAT</dt><dd class="mono">{{ preview()!.vatAmount | naira }}</dd></div> }
            <div class="grand"><dt>Total</dt><dd class="figure">{{ preview()?.total ?? null | naira }}</dd></div>
          </dl>

          @if (!quote) {
          <div class="form-grid one">
            <div class="field"><label for="pm">Paid by</label>
              <select id="pm" class="input" formControlName="paymentMethod"><option>Cash</option><option>Bank Transfer</option><option>Card</option><option>Credit</option></select></div>
            <div class="field"><label for="paid">Amount received now</label>
              <div class="inline"><input id="paid" class="input num" type="number" min="0" step="0.01" formControlName="paidNow" />
                <button type="button" class="btn btn-sm" [disabled]="!preview()" (click)="payInFull()">Full</button></div></div>
            @if ((preview()?.outstanding ?? 0) > 0) {
              <div class="field"><label for="due">Payment due by</label><input id="due" class="input" type="date" formControlName="dueDate" /></div>
            }
          </div>
          }

          @if (preview(); as p) {
            @if (p.previousBalance > 0 && !quote) {
              <p class="notice info split">Earlier debt: <strong>{{ p.previousBalance | naira }}</strong>.
                @if (p.appliedToPreviousBalance > 0) { {{ p.appliedToPreviousBalance | naira }} of this payment goes toward it, oldest invoice first. }</p>
            }
            @if (p.outstanding > 0 && !quote) { <p class="owed">Left unpaid on this sale: <strong class="mono">{{ p.outstanding | naira }}</strong></p> }
          }

          <button class="btn btn-primary big" type="submit" [disabled]="busy() || !request()">{{ busy() ? 'Saving…' : quote ? 'Save quotation' : 'Save sale' }}</button>
        </aside>
      </form>
    </div>

    <app-modal [open]="newCustomerOpen()" heading="New customer" (closed)="newCustomerOpen.set(false)">
      <form [formGroup]="custForm" (ngSubmit)="createCustomer()" id="cust-form" class="form-grid" novalidate>
        <div class="field span-2"><label for="cn">Name</label><input id="cn" class="input" formControlName="name" /></div>
        <div class="field"><label for="ct">Customer type</label><select id="ct" class="input" formControlName="customerType"><option>Retailer</option><option>Wholesaler</option><option>Distributor</option><option>Walk-in</option></select></div>
        <div class="field"><label for="cp">Phone</label><input id="cp" class="input" formControlName="phone" inputmode="tel" /></div>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="newCustomerOpen.set(false)">Cancel</button>
        <button type="submit" form="cust-form" class="btn btn-primary" [disabled]="custForm.invalid">Add customer</button>
      </ng-container>
    </app-modal>`,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 21rem; gap: 1rem; align-items: start; }
    .left { display: flex; flex-direction: column; gap: 1rem; min-width: 0; }
    .summary { position: sticky; top: 4.5rem; display: flex; flex-direction: column; gap: 1rem; }
    .one { grid-template-columns: minmax(0, 1fr); }
    .label { font: 600 .75rem/1 var(--font-body); letter-spacing: .04em; color: var(--ink-3); margin-bottom: .55rem; }
    .chip .stamp { margin-left: .5rem; }
    .chip { display: flex; justify-content: space-between; align-items: center; gap: 1rem; }
    .owes { color: var(--stamp); font-weight: 600; font-size: .8125rem; margin-top: .2rem; }
    .pick { list-style: none; margin: .5rem 0; padding: 0; border: 1px solid var(--line); border-radius: var(--r-2); max-height: 15rem; overflow: auto; background: #fff; }
    .pick:empty { display: none; }
    .pick button { display: flex; justify-content: space-between; gap: 1rem; width: 100%; padding: .6rem .8rem; background: none; border: 0; border-bottom: 1px solid var(--line); text-align: left; cursor: pointer; }
    .pick li:last-child button { border-bottom: 0; } .pick button:hover, .pick button:focus-visible { background: var(--brand-tint); }
    .results { margin: 0; border-radius: 0; border-left: 0; border-right: 0; }
    .sm { font-size: .75rem; } .low { color: var(--stamp); font-weight: 600; }
    .short { color: var(--stamp); font-weight: 600; font-size: .75rem; } .ovr { font-size: .6875rem; color: var(--signal-ink); background: var(--signal-tint); display: inline-block; padding: 0 .35rem; border-radius: 2px; margin-top: .2rem; }
    .serial { display: block; margin-top: .4rem; font-size: .75rem; color: var(--muted); } .serial textarea { margin-top: .2rem; font: .8125rem var(--font-mono); }
    .w-qty { width: 5rem; } .w-price { width: 8rem; }
    .totals { margin: 0; display: grid; gap: .35rem; } .totals div { display: flex; justify-content: space-between; } .totals dt { color: var(--muted); } .totals dd { margin: 0; }
    .totals .grand { border-top: 2px solid var(--ink); padding-top: .55rem; margin-top: .25rem; align-items: baseline; } .grand dt { font-weight: 700; color: var(--ink); } .grand dd { font-size: 2rem; color: var(--brand); }
    .inline { display: flex; gap: .4rem; } .split { font-size: .8125rem; } .owed { font-size: .875rem; }
    .big { min-height: 3rem; font-size: 1rem; }
    ul { margin: .4rem 0 0 1.1rem; padding: 0; }
    @media (max-width: 1100px) { .layout { grid-template-columns: minmax(0, 1fr); } .summary { position: static; } }
  `,
})
export class SaleNew implements OnInit {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  private readonly fb = inject(FormBuilder);
  protected readonly auth = inject(Auth);
  private readonly route = inject(ActivatedRoute);
  protected readonly quote = this.route.snapshot.data['mode'] === 'quote';
  protected readonly vatRate = VAT;

  protected readonly customers = signal<CustomerLookup[]>([]);
  protected readonly customer = signal<CustomerLookup | null>(null);
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly lines = signal<Line[]>([]);
  protected readonly results = signal<Product[]>([]);
  protected readonly preview = signal<SalePreview | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly shortfalls = signal<NonNullable<Problem['shortfalls']>>([]);
  protected readonly newCustomerOpen = signal(false);
  protected readonly advice = signal<{ kind: string; text: string }[]>([]);
  private adviceTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly form = this.fb.nonNullable.group({
    warehouseId: [0, Validators.min(1)],
    priceTier: ['Retailer' as Tier],
    paymentMethod: ['Cash'],
    discountPct: [0, [Validators.min(0), Validators.max(100)]],
    vat: [true],
    paidNow: [0, Validators.min(0)],
    dueDate: [''],
  });
  protected readonly custForm = this.fb.nonNullable.group({ name: ['', Validators.required], customerType: ['Retailer' as CustomerType], phone: [''] });
  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  protected readonly localSubtotal = computed(() => this.lines().reduce((s, l) => s + l.qty * l.price, 0));
  /** The request the server will price. Null until there is a customer, a warehouse and at least one valid line. */
  protected readonly request = computed<SaleRequest | null>(() => {
    const c = this.customer(); const f = this.formValue(); const lines = this.lines();
    if (!c || !lines.length || !(Number(f.warehouseId) > 0) || lines.some(l => !(l.qty > 0) || l.price < 0)) return null;
    return {
      customerId: c.id, priceTier: f.priceTier ?? 'Retailer', warehouseId: Number(f.warehouseId), paymentMethod: f.paymentMethod ?? 'Cash',
      discountPct: asNumber(f.discountPct), vatRate: f.vat ? VAT : 0, paidNow: asNumber(f.paidNow), dueDate: f.dueDate || null,
      lines: lines.map(l => ({ productId: l.productId, quantity: l.qty, unitPrice: l.price, ...(l.tracks && !this.quote ? { serials: splitSerials(l.serials) } : {}) })),
    };
  });

  private previewTimer: ReturnType<typeof setTimeout> | null = null;
  private productTimer: ReturnType<typeof setTimeout> | null = null;
  private customerTimer: ReturnType<typeof setTimeout> | null = null;
  private idem: { body: string; id: string } | null = null;
  private lastTier: Tier = 'Retailer';

  constructor() {
    // Ask the server to price the sale whenever anything that affects it changes.
    effect(() => {
      const r = this.request();
      if (this.previewTimer) clearTimeout(this.previewTimer);
      if (!r) { this.preview.set(null); return; }
      this.previewTimer = setTimeout(async () => {
        try { this.preview.set(await this.api.previewSale(r)); } catch { this.preview.set(null); }
      }, 250);
    });
    // Advice at the till: silent unless there is something worth saying (see AnalyticsService.SaleAdviceAsync).
    effect(() => {
      const lines = this.lines(); const c = this.customer();
      if (this.adviceTimer) clearTimeout(this.adviceTimer);
      if (this.quote || !lines.length) { this.advice.set([]); return; }
      this.adviceTimer = setTimeout(async () => {
        try { this.advice.set(await this.api2.saleAdvice(c?.id ?? null, lines.map(l => ({ productId: l.productId, quantity: l.qty })))); } catch { this.advice.set([]); }
      }, 700);
    });
    // Changing the price list reprices lines the user has not overridden.
    this.form.controls.priceTier.valueChanges.subscribe(t => this.retier(t));
  }

  async ngOnInit() {
    try {
      const [ws, cs] = await Promise.all([this.api.warehouses(), this.api.customerLookup()]);
      this.warehouses.set(ws); this.customers.set(cs);
      if (ws[0]) this.form.controls.warehouseId.setValue(ws[0].id);
    } catch (e) { this.error.set(messageOf(e)); }
  }

  // ---- customer ----
  protected searchCustomers(term: string) {
    if (this.customerTimer) clearTimeout(this.customerTimer);
    this.customerTimer = setTimeout(async () => { try { this.customers.set(await this.api.customerLookup(term)); } catch { /* keep the list */ } }, 250);
  }
  protected chooseCustomer(c: CustomerLookup) {
    this.customer.set(c);
    this.form.controls.priceTier.setValue(tierFor(c.customerType));
  }
  protected changeCustomer() { this.customer.set(null); }
  protected async createCustomer() {
    if (this.custForm.invalid) return;
    const v = this.custForm.getRawValue();
    try {
      const { id } = await this.api.createCustomer({
        name: v.name, customerType: v.customerType, phone: v.phone || null, contactName: null, location: null, address: null, email: null,
        taxId: null, rebateRatePct: 1, creditLimit: 0,
      });
      this.newCustomerOpen.set(false);
      this.chooseCustomer({ id, name: v.name.trim(), customerType: v.customerType, phone: v.phone || null, balance: 0 });
      this.custForm.reset({ name: '', customerType: 'Retailer', phone: '' });
      this.toasts.ok('Customer added.');
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  // ---- products ----
  protected priceOf(p: Product): number { return this.tierPrice(p, this.form.controls.priceTier.value); }
  private tierPrice(p: { priceDistributor: number; priceWholesaler: number; priceRetail: number }, t: Tier) {
    return t === 'Distributor' ? p.priceDistributor : t === 'Wholesaler' ? p.priceWholesaler : p.priceRetail;
  }

  protected searchProducts(term: string) {
    if (this.productTimer) clearTimeout(this.productTimer);
    const t = term.trim();
    if (t.length < 2) { this.results.set([]); return; }
    this.productTimer = setTimeout(async () => {
      try { this.results.set((await this.api.products({ search: t, pageSize: 8 })).items); } catch { this.results.set([]); }
    }, 220);
  }

  /** Scanners type the code and press Enter: a barcode adds the product straight away. */
  protected async addFromEnter(input: HTMLInputElement) {
    const code = input.value.trim();
    if (!code) return;
    try {
      const p = await this.api.productByBarcode(code);
      this.add(p); input.value = ''; this.results.set([]);
    } catch {
      if (this.results().length === 1) { this.add(this.results()[0]); input.value = ''; this.results.set([]); }
      else this.toasts.error(`No product with barcode “${code}”. Search by name instead.`);
    }
  }

  protected add(p: Product) {
    this.lines.update(ls => {
      const at = ls.findIndex(l => l.productId === p.id);
      if (at >= 0) return ls.map((l, i) => (i === at ? { ...l, qty: l.qty + 1 } : l));
      const tiers = { Distributor: p.priceDistributor, Wholesaler: p.priceWholesaler, Retailer: p.priceRetail };
      const price = this.tierPrice(p, this.form.controls.priceTier.value);
      return [...ls, { productId: p.id, name: p.name, sku: p.sku, unit: p.unit, qty: 1, price, standard: price, stock: p.totalQuantity, tracks: p.tracksSerial, serials: '', tiers }];
    });
  }

  protected setQty(i: number, v: string) { this.patchLine(i, { qty: Math.max(0, Math.floor(asNumber(v))) }); }
  protected setPrice(i: number, v: string) { this.patchLine(i, { price: Math.max(0, asNumber(v)) }); }
  protected setSerials(i: number, v: string) { this.patchLine(i, { serials: v }); }
  protected serialCount(l: Line) { return splitSerials(l.serials).length; }
  protected remove(i: number) { this.lines.update(ls => ls.filter((_, j) => j !== i)); }
  private patchLine(i: number, p: Partial<Line>) { this.lines.update(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l))); }

  private retier(t: Tier) {
    this.lines.update(ls => ls.map(l => {
      const overridden = l.price !== l.standard;   // keep what the seller typed
      const standard = l.tiers[t];
      return { ...l, standard, price: overridden ? l.price : standard };
    }));
    this.lastTier = t;
  }

  protected payInFull() { const p = this.preview(); if (p) this.form.controls.paidNow.setValue(p.total + p.previousBalance > 0 ? p.total : 0); }

  // ---- save ----
  protected async save() {
    const r = this.request();
    if (!r || this.busy()) return;
    this.busy.set(true); this.error.set(''); this.shortfalls.set([]);
    try {
      // One key per distinct order: a retry after a dropped connection is safe, an edited order is a new sale.
      const body = JSON.stringify(r);
      if (!this.idem || this.idem.body !== body) this.idem = { body, id: crypto.randomUUID() };
      if (this.quote) {
        const q = await this.api2.createQuotation({ customerId: r.customerId, priceTier: r.priceTier, discountPct: r.discountPct, vatRate: r.vatRate, lines: r.lines });
        this.toasts.ok(`Quotation ${q.number} saved.`);
        await this.router.navigate(['/quotations']);
        return;
      }
      const res = await this.api.createSale(r, this.idem.id);
      this.toasts.ok(`Sale ${res.invoiceNumber} saved.`);
      await this.router.navigate(['/sales', res.invoiceId]);
    } catch (e) {
      const p = problemOf(e);
      if (p?.shortfalls?.length) this.shortfalls.set(p.shortfalls); else this.error.set(messageOf(e));
    } finally { this.busy.set(false); }
  }
}
