import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Auth } from '../../core/auth.service';
import { Category } from '../../core/models';
import { PriceBook, PriceBookRow, PriceHistory } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { SendEmail } from '../quotations/send-email';
import { PagedList } from '../../shared/paged-list';
import { NairaPipe, Pager, StampTimePipe } from '../../shared/ui';

interface Edit { d: number; w: number; r: number; }
interface NewRow { name: string; categoryId: number; unit: string; d: number; w: number; r: number; }

/** Same rule as the server (PriceBookService.Adjusted): nearest step, halves round up, never below zero. */
function adjusted(price: number, pct: number, step: number): number {
  let v = price * (1 + pct / 100);
  if (step > 0) v = Math.round(v / step) * step;
  return Math.max(0, Math.round(v * 100) / 100);
}

@Component({
  selector: 'app-pricebook',
  imports: [FormsModule, Icon, Modal, NairaPipe, StampTimePipe, Pager, SendEmail],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Price book</h1>
        <div class="actions">
          <select class="input tier" aria-label="Price list to print" [ngModel]="pdfTier()" (ngModelChange)="pdfTier.set($event)"><option>Retailer</option><option>Wholesaler</option><option>Distributor</option></select>
          <a class="btn" [href]="'/api/price-list.pdf?tier=' + pdfTier()" target="_blank" rel="noopener"><app-icon name="download" [size]="18" /> Download PDF</a>
          <button type="button" class="btn" (click)="emailOpen.set(true)"><app-icon name="arrow" [size]="18" /> Email price list</button>
          @if (auth.isAdmin()) { <button type="button" class="btn btn-primary" (click)="openAdd()"><app-icon name="plus" [size]="18" /> Add products</button> }
        </div>
      </div>
      @if (data(); as d) {
        <p class="muted lede">@if (d.lastUpdated) { Prices last changed <strong>{{ d.lastUpdated | stampTime }}</strong>. } {{ d.changesThisMonth }} change(s) this month.</p>
      }

      <div class="tabs" role="tablist">
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'prices'" (click)="tab.set('prices')">Current prices</button>
        @if (auth.isAdmin()) { <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'history'" (click)="openHistory()">History</button> }
      </div>

      @if (tab() === 'prices') {
        <section class="card">
          <div class="toolbar">
            <div class="search grow"><app-icon name="search" [size]="17" /><input class="input" type="search" placeholder="Search product, code or category" aria-label="Search price book" (input)="onSearch($any($event.target).value)" /></div>
            @if (auth.isAdmin()) {
              <div class="bulk">
                <label class="lab" for="bp">Move every price by</label>
                <input id="bp" class="input num pct" type="number" step="0.5" [ngModel]="pct()" (ngModelChange)="pct.set($event)" /> %
                <label class="lab" for="bs">Round to</label>
                <select id="bs" class="input" [ngModel]="step()" (ngModelChange)="step.set(+$event)"><option [ngValue]="0">the kobo</option><option [ngValue]="1">₦1</option><option [ngValue]="10">₦10</option><option [ngValue]="50">₦50</option><option [ngValue]="100">₦100</option></select>
                <button type="button" class="btn btn-sm" [disabled]="!pct()" (click)="applyPct()">Preview</button>
              </div>
            }
          </div>
          @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Product</th><th>Category</th><th class="num">Distributor</th><th class="num">Wholesaler</th><th class="num">Retail</th><th class="num">In stock</th></tr></thead>
            <tbody>
              @for (p of data()?.page?.items ?? []; track p.productId) {
                @let e = edits()[p.productId];
                <tr [class.changed]="!!e">
                  <td><span class="strong">{{ p.product }}</span><div class="muted mono sm">{{ p.sku }}</div></td><td>{{ p.category }}</td>
                  @if (auth.isAdmin()) {
                    <td class="num"><input class="input num cell" type="number" min="0" step="0.01" [value]="e?.d ?? p.distributor" [attr.aria-label]="'Distributor price of ' + p.product" (input)="edit(p, 'd', $any($event.target).value)" /></td>
                    <td class="num"><input class="input num cell" type="number" min="0" step="0.01" [value]="e?.w ?? p.wholesaler" [attr.aria-label]="'Wholesaler price of ' + p.product" (input)="edit(p, 'w', $any($event.target).value)" /></td>
                    <td class="num"><input class="input num cell" type="number" min="0" step="0.01" [value]="e?.r ?? p.retail" [attr.aria-label]="'Retail price of ' + p.product" (input)="edit(p, 'r', $any($event.target).value)" /></td>
                  } @else {
                    <td class="num mono">{{ p.distributor | naira }}</td><td class="num mono">{{ p.wholesaler | naira }}</td><td class="num mono">{{ p.retail | naira }}</td>
                  }
                  <td class="num mono">{{ p.inStock }} <span class="muted">{{ p.unit }}</span></td>
                </tr>
              }
            </tbody>
          </table></div>
          @if (!loading() && !(data()?.page?.items?.length)) { <div class="empty"><strong>No products on the price list</strong>Add products to start the book.</div> }
          <app-pager [page]="page()" [pageSize]="pageSize" [total]="data()?.page?.total ?? 0" (pageChange)="goTo($event)" />
          @if (editCount()) {
            <div class="savebar"><span><strong>{{ editCount() }}</strong> product(s) with new prices</span>
              <input class="input note" placeholder="Why? (optional, kept in the history)" aria-label="Reason for the change" [ngModel]="note()" (ngModelChange)="note.set($event)" />
              <button type="button" class="btn" (click)="edits.set({})">Discard</button>
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="save()">Save prices</button></div>
          }
        </section>
      } @else {
        <section class="card">
          <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" /><input class="input" type="search" placeholder="Search product or note" aria-label="Search history" (input)="history.setSearch($any($event.target).value)" /></div></div>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>When</th><th>Product</th><th class="num">Distributor</th><th class="num">Wholesaler</th><th class="num">Retail</th><th>By</th></tr></thead>
            <tbody>@for (h of history.items(); track h.id) {
              <tr><td>{{ h.changedAt | stampTime }}</td><td class="strong">{{ h.product }}<div class="muted sm">{{ h.note }}</div></td>
                <td class="num mono">{{ h.distributorWas | naira }} → {{ h.distributorNow | naira }}</td><td class="num mono">{{ h.wholesalerWas | naira }} → {{ h.wholesalerNow | naira }}</td>
                <td class="num mono">{{ h.retailWas | naira }} → {{ h.retailNow | naira }}</td><td>{{ h.changedBy }}</td></tr>
            }</tbody>
          </table></div>
          @if (!history.loading() && !history.items().length) { <div class="empty"><strong>No price changes yet</strong></div> }
          <app-pager [page]="history.page()" [pageSize]="history.pageSize" [total]="history.total()" (pageChange)="history.goTo($event)" />
        </section>
      }
    </div>

    <app-send-email [open]="emailOpen()" mode="price-list" (closed)="emailOpen.set(false)" />

    <app-modal [open]="addOpen()" heading="Add products to the price list" [wide]="true" (closed)="addOpen.set(false)">
      <p class="muted sm">One row per product. Each gets its own code and barcode; stock is added later when goods are received.</p>
      <div class="table-wrap"><table class="table grid">
        <thead><tr><th>Name</th><th>Category</th><th>Unit</th><th class="num">Distributor</th><th class="num">Wholesaler</th><th class="num">Retail</th></tr></thead>
        <tbody>
          @for (r of rows(); track $index; let i = $index) {
            <tr>
              <td><input class="input" [value]="r.name" [attr.aria-label]="'Product name, row ' + (i + 1)" (input)="setRow(i, 'name', $any($event.target).value)" /></td>
              <td><select class="input" [attr.aria-label]="'Category, row ' + (i + 1)" (change)="setRow(i, 'categoryId', +$any($event.target).value)">@for (c of categories(); track c.id) { <option [value]="c.id" [selected]="c.id === r.categoryId">{{ c.name }}</option> }</select></td>
              <td><select class="input" [attr.aria-label]="'Unit, row ' + (i + 1)" (change)="setRow(i, 'unit', $any($event.target).value)">@for (u of units; track u) { <option [selected]="u === r.unit">{{ u }}</option> }</select></td>
              <td><input class="input num" type="number" min="0" step="0.01" [value]="r.d" (input)="setRow(i, 'd', +$any($event.target).value)" /></td>
              <td><input class="input num" type="number" min="0" step="0.01" [value]="r.w" (input)="setRow(i, 'w', +$any($event.target).value)" /></td>
              <td><input class="input num" type="number" min="0" step="0.01" [value]="r.r" (input)="setRow(i, 'r', +$any($event.target).value)" /></td>
            </tr>
          }
        </tbody>
      </table></div>
      <button type="button" class="btn btn-sm" (click)="addRow()"><app-icon name="plus" [size]="16" /> Another row</button>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="addOpen.set(false)">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="!filled().length || busy()" (click)="saveNew()">Add {{ filled().length }} product(s)</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="!!preview()" heading="Move every price?" (closed)="preview.set(null)">
      @if (preview(); as pv) {
        <p>{{ pv.count }} product(s) on this page will move by <strong>{{ pct() }}%</strong>. Nothing is saved until you press “Save prices”.</p>
      }
      <ng-container modal-actions><button type="button" class="btn" (click)="preview.set(null)">Cancel</button><button type="button" class="btn btn-primary" (click)="confirmPct()">Apply to the grid</button></ng-container>
    </app-modal>`,
  styles: `
    .tier { width: auto; } .lede { margin: 0 0 1rem; } .sm { font-size: .75rem; } .cell { width: 8.5rem; } tr.changed { background: var(--signal-tint); }
    .bulk { display: flex; align-items: center; gap: .4rem; flex-wrap: wrap; } .lab { font-size: .8125rem; color: var(--muted); margin: 0; } .pct { width: 5.5rem; }
    .savebar { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; padding: .8rem 1.125rem; border-top: 2px solid var(--brand); background: var(--brand-tint); position: sticky; bottom: 0; }
    .savebar .note { flex: 1 1 14rem; } .grid .input { min-width: 6rem; }
  `,
})
export class PricebookPage implements OnInit {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);
  protected readonly auth = inject(Auth);
  protected readonly pageSize = 50;
  protected readonly units = ['Bag', 'Pack', 'Piece', 'Kg', 'Carton', 'Bottle', 'Tin', 'Sachet'];
  protected readonly tab = signal<'prices' | 'history'>('prices');
  protected readonly pdfTier = signal('Retailer');
  protected readonly emailOpen = signal(false);
  protected readonly data = signal<PriceBook | null>(null);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly busy = signal(false);
  protected readonly edits = signal<Record<number, Edit>>({});
  protected readonly editCount = computed(() => Object.keys(this.edits()).length);
  protected readonly note = signal('');
  protected readonly pct = signal(0);
  protected readonly step = signal(0);
  protected readonly preview = signal<{ count: number } | null>(null);
  protected readonly categories = signal<Category[]>([]);
  protected readonly addOpen = signal(false);
  protected readonly rows = signal<NewRow[]>([]);
  protected readonly filled = computed(() => this.rows().filter(r => r.name.trim()));
  protected readonly history = new PagedList<PriceHistory>(q => this.api2.priceHistory(q), 20);
  private term = '';
  private timer: ReturnType<typeof setTimeout> | null = null;

  async ngOnInit() {
    void this.load();
    try { this.categories.set(await this.api.categories()); } catch { /* the add dialog needs them, the grid does not */ }
  }

  private async load() {
    this.loading.set(true);
    try { this.data.set(await this.api2.priceBook({ page: this.page(), pageSize: this.pageSize, search: this.term })); this.error.set(''); }
    catch (e) { this.error.set(messageOf(e)); } finally { this.loading.set(false); }
  }
  protected onSearch(v: string) { this.term = v; if (this.timer) clearTimeout(this.timer); this.timer = setTimeout(() => { this.page.set(1); void this.load(); }, 300); }
  protected goTo(p: number) { this.page.set(p); void this.load(); }
  protected openHistory() { this.tab.set('history'); void this.history.load(); }

  protected edit(p: PriceBookRow, f: 'd' | 'w' | 'r', raw: string) {
    const v = Math.max(0, Number(raw) || 0);
    this.edits.update(m => {
      const cur = m[p.productId] ?? { d: p.distributor, w: p.wholesaler, r: p.retail };
      const next = { ...cur, [f]: v };
      const copy = { ...m };
      if (next.d === p.distributor && next.w === p.wholesaler && next.r === p.retail) delete copy[p.productId]; else copy[p.productId] = next;
      return copy;
    });
  }

  protected applyPct() { this.preview.set({ count: this.data()?.page.items.length ?? 0 }); }
  protected confirmPct() {
    const m: Record<number, Edit> = { ...this.edits() };
    for (const p of this.data()?.page.items ?? []) {
      const n = { d: adjusted(p.distributor, this.pct(), this.step()), w: adjusted(p.wholesaler, this.pct(), this.step()), r: adjusted(p.retail, this.pct(), this.step()) };
      if (n.d === p.distributor && n.w === p.wholesaler && n.r === p.retail) delete m[p.productId]; else m[p.productId] = n;
    }
    this.edits.set(m); this.preview.set(null);
  }

  protected async save() {
    const updates = Object.entries(this.edits()).map(([id, e]) => ({ productId: Number(id), distributor: e.d, wholesaler: e.w, retail: e.r }));
    if (!updates.length) return;
    this.busy.set(true);
    try { const r = await this.api2.applyPrices(updates, this.note().trim() || null); this.toasts.ok(`${r.changed} price(s) updated.`); this.edits.set({}); this.note.set(''); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected openAdd() { this.rows.set(Array.from({ length: 5 }, () => this.blank())); this.addOpen.set(true); }
  private blank(): NewRow { return { name: '', categoryId: this.categories()[0]?.id ?? 0, unit: 'Bag', d: 0, w: 0, r: 0 }; }
  protected addRow() { this.rows.update(r => [...r, this.blank()]); }
  protected setRow(i: number, f: keyof NewRow, v: string | number) { this.rows.update(rs => rs.map((r, j) => (j === i ? { ...r, [f]: v } : r))); }

  protected async saveNew() {
    this.busy.set(true);
    try {
      const r = await this.api2.addPriceProducts(this.filled().map(x => ({ name: x.name.trim(), categoryId: x.categoryId, unit: x.unit, distributor: x.d, wholesaler: x.w, retail: x.r })), null);
      this.toasts.ok(`Added ${r.ids.length} product(s).`); this.addOpen.set(false); await this.load();
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }
}
