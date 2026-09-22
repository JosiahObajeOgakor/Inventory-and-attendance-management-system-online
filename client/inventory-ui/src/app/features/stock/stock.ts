import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { BatchRow, MovementRow, Product, Warehouse } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { DayPipe, Pager, Stamp, StampTimePipe } from '../../shared/ui';

type Tab = 'batches' | 'movements';
type Dialog = 'production' | 'transfer' | 'adjust' | null;

@Component({
  selector: 'app-stock',
  imports: [ReactiveFormsModule, Icon, Modal, Stamp, Pager, DayPipe, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Stock</h1>
        <div class="actions">
          <a class="btn" href="/api/exports/inventory" download><app-icon name="download" [size]="18" /> Excel</a>
          @if (auth.company() !== 'candid') { <button type="button" class="btn btn-primary" (click)="open('production')"><app-icon name="plus" [size]="18" /> Record production</button> }
          <button type="button" class="btn" (click)="open('transfer')"><app-icon name="swap" [size]="18" /> Move between warehouses</button>
        </div>
      </div>

      <div class="tabs" role="tablist">
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'batches'" (click)="setTab('batches')">By batch</button>
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'movements'" (click)="setTab('movements')">History</button>
      </div>

      <section class="card">
        <div class="toolbar">
          <div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search product, SKU or batch" aria-label="Search stock" (input)="current().setSearch($any($event.target).value)" /></div>
          @if (tab() === 'batches') { <label class="check"><input type="checkbox" (change)="onlyLow($any($event.target).checked)" /> Only low or expiring</label> }
        </div>

        @if (batches.error() || moves.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ batches.error() || moves.error() }}</p> }

        @if (tab() === 'batches') {
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Product</th><th>Warehouse</th><th>Batch</th><th>Expires</th><th class="num">On hand</th><th>Status</th>@if (auth.isAdmin()) { <th><span class="sr-only">Actions</span></th> }</tr></thead>
            <tbody>
              @for (b of batches.items(); track b.batchId) {
                <tr><td><span class="strong">{{ b.product }}</span><div class="muted mono sm">{{ b.sku }} · {{ b.category }}</div></td><td>{{ b.warehouse }}</td>
                  <td class="mono">{{ b.batchNumber }}</td><td>{{ b.expiryDate ? (b.expiryDate | day) : '—' }}</td><td class="num mono strong">{{ b.quantity }}</td>
                  <td><app-stamp [label]="b.status" /></td>
                  @if (auth.isAdmin()) { <td class="actions"><button type="button" class="btn btn-sm" (click)="openAdjust(b)">Adjust</button></td> }</tr>
              }
            </tbody>
          </table></div>
          @if (!batches.loading() && !batches.items().length) { <div class="empty"><strong>No stock found</strong>Record a purchase or production run to bring stock in.</div> }
          <app-pager [page]="batches.page()" [pageSize]="batches.pageSize" [total]="batches.total()" (pageChange)="batches.goTo($event)" />
        } @else {
          <div class="table-wrap"><table class="table">
            <thead><tr><th>When</th><th>Product</th><th>Warehouse</th><th>Movement</th><th class="num">Qty</th><th>Reason</th><th>By</th></tr></thead>
            <tbody>
              @for (m of moves.items(); track m.id) {
                <tr><td>{{ m.at | stampTime }}</td><td><span class="strong">{{ m.product }}</span><div class="muted mono sm">{{ m.sku }}</div></td><td>{{ m.warehouse }}</td>
                  <td><span class="stamp" [class]="m.type === 'IN' ? 'stamp-ok' : m.type === 'OUT' ? 'stamp-info' : 'stamp-warn'">{{ m.type === 'IN' ? 'In' : m.type === 'OUT' ? 'Out' : 'Adjust' }}</span></td>
                  <td class="num mono">{{ m.quantity }}</td><td>{{ reason(m) }}</td><td>{{ m.user ?? '—' }}</td></tr>
              }
            </tbody>
          </table></div>
          @if (!moves.loading() && !moves.items().length) { <div class="empty"><strong>No movements yet</strong>Every sale, receipt, transfer and adjustment is logged here.</div> }
          <app-pager [page]="moves.page()" [pageSize]="moves.pageSize" [total]="moves.total()" (pageChange)="moves.goTo($event)" />
        }
      </section>
    </div>

    <!-- production -->
    <app-modal [open]="dialog() === 'production'" heading="Record production" (closed)="close()">
      <form id="prod" [formGroup]="prodForm" (ngSubmit)="saveProduction()" class="form-grid" novalidate>
        <div class="field span-2"><label for="pp">Product</label><select id="pp" class="input" formControlName="productId">@for (p of products(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }</select></div>
        <div class="field"><label for="pw">Into warehouse</label><select id="pw" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
        <div class="field"><label for="pq">Quantity produced</label><input id="pq" class="input num" type="number" min="1" formControlName="quantity" /></div>
        <div class="field"><label for="pd">Production date</label><input id="pd" class="input" type="date" formControlName="producedOn" /></div>
        <div class="field"><label for="pb">Batch number</label><input id="pb" class="input" formControlName="batchNumber" /><span class="hint">Leave blank to name it after the date.</span></div>
        <div class="field"><label for="pe">Best before</label><input id="pe" class="input" type="date" formControlName="expiryDate" /></div>
      </form>
      <ng-container modal-actions><button type="button" class="btn" (click)="close()">Cancel</button><button type="submit" form="prod" class="btn btn-primary" [disabled]="prodForm.invalid || busy()">Add to stock</button></ng-container>
    </app-modal>

    <!-- transfer -->
    <app-modal [open]="dialog() === 'transfer'" heading="Move stock" (closed)="close()">
      <form id="xfer" [formGroup]="xferForm" (ngSubmit)="saveTransfer()" class="form-grid" novalidate>
        <div class="field span-2"><label for="xp">Product</label><select id="xp" class="input" formControlName="productId">@for (p of products(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }</select></div>
        <div class="field"><label for="xf">From</label><select id="xf" class="input" formControlName="fromWarehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
        <div class="field"><label for="xt">To</label><select id="xt" class="input" formControlName="toWarehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
        <div class="field"><label for="xq">Quantity</label><input id="xq" class="input num" type="number" min="1" formControlName="quantity" /><span class="hint">Batches with the earliest expiry move first.</span></div>
      </form>
      <ng-container modal-actions><button type="button" class="btn" (click)="close()">Cancel</button><button type="submit" form="xfer" class="btn btn-primary" [disabled]="xferForm.invalid || busy()">Move stock</button></ng-container>
    </app-modal>

    <!-- adjust -->
    <app-modal [open]="dialog() === 'adjust'" heading="Adjust stock" (closed)="close()">
      @if (adjusting(); as b) {
        <p class="muted">{{ b.product }} · batch {{ b.batchNumber }} · {{ b.warehouse }} — currently <strong class="mono">{{ b.quantity }}</strong></p>
        <form id="adj" [formGroup]="adjForm" (ngSubmit)="saveAdjust()" class="form-grid" style="margin-top:.9rem" novalidate>
          <div class="field"><label for="ad">Change by</label><input id="ad" class="input num" type="number" step="1" formControlName="delta" /><span class="hint">Use a minus sign to remove stock.</span></div>
          <div class="field span-2"><label for="ar">Reason</label><input id="ar" class="input" formControlName="reason" placeholder="e.g. Stock count: 2 bags damaged" /></div>
        </form>
      }
      <ng-container modal-actions><button type="button" class="btn" (click)="close()">Cancel</button><button type="submit" form="adj" class="btn btn-primary" [disabled]="adjForm.invalid || busy()">Save adjustment</button></ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; }`,
})
export class StockPage implements OnInit {
  private readonly api = inject(Api);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  protected readonly auth = inject(Auth);

  protected readonly tab = signal<Tab>('batches');
  private low = false;
  protected readonly batches = new PagedList<BatchRow>(q => this.api.batches({ ...q, onlyLow: this.low || undefined }));
  protected readonly moves = new PagedList<MovementRow>(q => this.api.movements(q), 20);
  protected readonly products = signal<Product[]>([]);
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly dialog = signal<Dialog>(null);
  protected readonly adjusting = signal<BatchRow | null>(null);
  protected readonly busy = signal(false);

  protected readonly prodForm = this.fb.nonNullable.group({
    productId: [0, Validators.min(1)], warehouseId: [0, Validators.min(1)], quantity: [1, Validators.min(1)],
    producedOn: [''], batchNumber: [''], expiryDate: [''],
  });
  protected readonly xferForm = this.fb.nonNullable.group({
    productId: [0, Validators.min(1)], fromWarehouseId: [0, Validators.min(1)], toWarehouseId: [0, Validators.min(1)], quantity: [1, Validators.min(1)],
  });
  protected readonly adjForm = this.fb.nonNullable.group({ delta: [0, [Validators.required]], reason: ['', [Validators.required, Validators.minLength(3)]] });

  ngOnInit() {
    void this.batches.load();
    void this.api.warehouses().then(w => this.warehouses.set(w));
    void this.api.products({ pageSize: 200 }).then(p => this.products.set(p.items));
  }

  protected current() { return this.tab() === 'batches' ? this.batches : this.moves; }
  protected setTab(t: Tab) { this.tab.set(t); void (t === 'batches' ? this.batches : this.moves).load(); }
  protected onlyLow(v: boolean) { this.low = v; this.batches.page.set(1); void this.batches.load(); }

  protected reason(m: MovementRow): string {
    if (m.note) return m.note;
    switch (m.referenceType) {
      case 'Invoice': return 'Sale';
      case 'PurchaseOrder': return 'Purchase received';
      case 'Production': return 'Production';
      case 'Transfer': return m.type === 'OUT' ? 'Transfer out' : 'Transfer in';
      case 'OpeningBalance': return 'Opening stock';
      case 'InvoiceVoid': return 'Sale voided';
      default: return m.referenceType ?? '—';
    }
  }

  protected open(d: Dialog) {
    const w = this.warehouses()[0]?.id ?? 0; const p = this.products()[0]?.id ?? 0;
    if (d === 'production') this.prodForm.patchValue({ productId: p, warehouseId: w, quantity: 1, producedOn: '', batchNumber: '', expiryDate: '' });
    if (d === 'transfer') this.xferForm.patchValue({ productId: p, fromWarehouseId: w, toWarehouseId: this.warehouses()[1]?.id ?? 0, quantity: 1 });
    this.dialog.set(d);
  }
  protected openAdjust(b: BatchRow) { this.adjusting.set(b); this.adjForm.reset({ delta: 0, reason: '' }); this.dialog.set('adjust'); }
  protected close() { this.dialog.set(null); }

  private async run(work: () => Promise<unknown>, done: string) {
    this.busy.set(true);
    try { await work(); this.toasts.ok(done); this.close(); await Promise.all([this.batches.load(), this.tab() === 'movements' ? this.moves.load() : Promise.resolve()]); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected saveProduction() {
    const v = this.prodForm.getRawValue();
    return this.run(() => this.api.produce({ ...v, producedOn: v.producedOn || null, batchNumber: v.batchNumber || null, expiryDate: v.expiryDate || null }), 'Production added to stock.');
  }
  protected saveTransfer() { return this.run(() => this.api.transfer(this.xferForm.getRawValue()), 'Stock moved.'); }
  protected saveAdjust() {
    const b = this.adjusting(); if (!b) return;
    const v = this.adjForm.getRawValue();
    return this.run(() => this.api.adjust({ batchId: b.batchId, delta: Number(v.delta), reason: v.reason }), 'Stock adjusted.');
  }
}
