import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { Product, Warehouse } from '../../core/models';
import { Serial, SerialDiscrepancy } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe } from '../../shared/ui';

const STATUS_STAMP: Record<string, string> = { 'In Stock': 'stamp-ok', Sold: 'stamp-info', Returned: 'stamp-warn', 'Written Off': 'stamp-bad' };

@Component({
  selector: 'app-serials',
  imports: [ReactiveFormsModule, Icon, Modal, DayPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Serial numbers</h1></div>
      <p class="muted lede">Scales, feeders and other units are tracked one by one. Receive them here; pick the exact serials when you sell.</p>

      <div class="tabs" role="tablist">
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'units'" (click)="tab.set('units')">Units</button>
        @if (auth.isAdmin()) { <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'check'" (click)="openCheck()">Stock check</button> }
      </div>

      @if (tab() === 'units') {
        <section class="card">
          <div class="toolbar">
            <div class="field inline"><label for="sp">Product</label>
              <select id="sp" class="input" (change)="pick($any($event.target).value)">
                <option value="">Choose a tracked product…</option>@for (p of products(); track p.id) { <option [value]="p.id">{{ p.name }} ({{ p.sku }})</option> }</select></div>
            <div class="field inline"><label for="ss">Show</label>
              <select id="ss" class="input" (change)="setStatus($any($event.target).value)"><option value="">All</option><option>In Stock</option><option>Sold</option><option>Returned</option><option>Written Off</option></select></div>
            <div class="search grow"><app-icon name="search" [size]="17" /><input class="input" type="search" placeholder="Find a serial" aria-label="Find a serial" (input)="setSearch($any($event.target).value)" /></div>
            <button type="button" class="btn btn-primary" [disabled]="!product()" (click)="openReceive()"><app-icon name="plus" [size]="18" /> Receive units</button>
          </div>
          @if (!products().length && !loading()) { <div class="notice info" style="margin:1rem">No product tracks serial numbers yet. Turn on “Tracks serial numbers” when editing a product.</div> }
          @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Serial</th><th>Status</th><th>Where</th><th>Received</th><th>Sold to</th><th><span class="sr-only">Actions</span></th></tr></thead>
            <tbody>
              @for (s of serials(); track s.id) {
                <tr><td class="mono strong">{{ s.serialNumber }}</td><td><span class="stamp" [class]="'stamp ' + stamp(s.status)">{{ s.status }}</span></td>
                  <td>{{ s.warehouse ?? '—' }}</td><td>{{ s.receivedAt | day }}</td>
                  <td>@if (s.invoiceNumber) { <span class="mono">{{ s.invoiceNumber }}</span><div class="muted sm">{{ s.customer }} · {{ s.soldAt | day }}</div> } @else { — }</td>
                  <td class="actions">
                    @if (s.status === 'Sold') { <button type="button" class="btn btn-sm" (click)="openTakeBack(s)">Take back</button> }
                    @if (auth.isAdmin() && (s.status === 'In Stock' || s.status === 'Returned')) { <button type="button" class="btn btn-sm btn-danger" (click)="writeOff(s)">Write off</button> }
                  </td></tr>
              }
            </tbody>
          </table></div>
          @if (product() && !loading() && !serials().length) { <div class="empty"><strong>No units match</strong>Receive units to register their serial numbers.</div> }
        </section>
      } @else {
        <section class="card">
          <p class="lede pad muted">Where the stock count and the serial register disagree. A shortfall means a unit was sold or moved without its serial being recorded.</p>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Product</th><th class="num">Counted stock</th><th class="num">Serials on the shelf</th><th class="num">Difference</th></tr></thead>
            <tbody>@for (d of discrepancies(); track d.productId) {
              <tr><td class="strong">{{ d.product }} <span class="muted mono sm">{{ d.sku }}</span></td><td class="num mono">{{ d.countedStock }}</td><td class="num mono">{{ d.serialsOnShelf }}</td><td class="num mono owes">{{ d.difference > 0 ? '+' : '' }}{{ d.difference }}</td></tr>
            }</tbody>
          </table></div>
          @if (!discrepancies().length) { <div class="empty"><strong>Everything agrees</strong>Every tracked product’s count matches its serials.</div> }
        </section>
      }
    </div>

    <app-modal [open]="receiveOpen()" heading="Receive units" (closed)="receiveOpen.set(false)">
      <form id="rf" [formGroup]="rform" (ngSubmit)="receive()" class="form-grid" novalidate>
        <div class="field span-2"><label for="rw">Into warehouse</label><select id="rw" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
        <div class="field span-2"><label for="rs">Serial numbers <span class="muted">— paste a list, one per line or separated by commas ({{ rcount() }})</span></label><textarea id="rs" class="input mono" rows="7" formControlName="serials"></textarea></div>
        <div class="field span-2"><label for="rn">Note</label><input id="rn" class="input" formControlName="notes" /></div>
      </form>
      <p class="muted sm">This registers the serials only. Book the matching quantity in with a purchase or a stock adjustment so the counts agree.</p>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="receiveOpen.set(false)">Cancel</button>
        <button type="submit" form="rf" class="btn btn-primary" [disabled]="rform.invalid || !rcount() || busy()">Register {{ rcount() }} unit(s)</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="!!takeBack()" heading="Take a unit back" (closed)="takeBack.set(null)">
      @if (takeBack(); as s) {
        <p>Serial <strong class="mono">{{ s.serialNumber }}</strong> came back from {{ s.customer ?? 'a customer' }}. It goes back on the shelf as “Returned”.</p>
        <form id="tf" [formGroup]="tform" (ngSubmit)="doTakeBack()" class="form-grid" novalidate>
          <div class="field"><label for="tw">Back into</label><select id="tw" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
          <div class="field"><label for="tr">Reason</label><input id="tr" class="input" formControlName="reason" /></div>
        </form>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="takeBack.set(null)">Cancel</button>
        <button type="submit" form="tf" class="btn btn-primary" [disabled]="busy()">Take back</button>
      </ng-container>
    </app-modal>`,
  styles: `.lede { margin: 0 0 1rem; max-width: 46rem; } .lede.pad { padding: 1rem 1.125rem 0; margin: 0; } .sm { font-size: .75rem; } .owes { color: var(--stamp); font-weight: 600; }
    .inline { display: flex; align-items: center; gap: .4rem; } .inline label { margin: 0; } .actions { white-space: nowrap; }`,
})
export class SerialsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly auth = inject(Auth);
  protected readonly tab = signal<'units' | 'check'>('units');
  protected readonly products = signal<Product[]>([]);
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly product = signal<Product | null>(null);
  protected readonly serials = signal<Serial[]>([]);
  protected readonly discrepancies = signal<SerialDiscrepancy[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly busy = signal(false);
  protected readonly receiveOpen = signal(false);
  protected readonly takeBack = signal<Serial | null>(null);
  protected readonly rform = this.fb.nonNullable.group({ warehouseId: [0, Validators.min(1)], serials: ['', Validators.required], notes: [''] });
  protected readonly tform = this.fb.nonNullable.group({ warehouseId: [0, Validators.min(1)], reason: [''] });
  private readonly text = signal('');
  protected readonly rcount = computed(() => split(this.text()).length);
  private status = '';
  private term = '';
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() { this.rform.controls.serials.valueChanges.subscribe(v => this.text.set(v)); }

  async ngOnInit() {
    try {
      const [ws, ps] = await Promise.all([this.api.warehouses(), this.api.products({ pageSize: 100 })]);
      this.warehouses.set(ws); this.products.set(ps.items.filter(p => p.tracksSerial && p.isActive));
      if (ws[0]) { this.rform.controls.warehouseId.setValue(ws[0].id); this.tform.controls.warehouseId.setValue(ws[0].id); }
    } catch (e) { this.error.set(messageOf(e)); } finally { this.loading.set(false); }
  }

  protected stamp(s: string) { return STATUS_STAMP[s] ?? 'stamp-info'; }
  protected async pick(id: string) { this.product.set(this.products().find(p => p.id === Number(id)) ?? null); await this.load(); }
  protected setStatus(v: string) { this.status = v; void this.load(); }
  protected setSearch(v: string) { this.term = v; if (this.timer) clearTimeout(this.timer); this.timer = setTimeout(() => void this.load(), 300); }

  private async load() {
    const p = this.product(); if (!p) { this.serials.set([]); return; }
    try { this.serials.set(await this.api2.serials(p.id, { status: this.status || undefined, search: this.term || undefined })); this.error.set(''); } catch (e) { this.error.set(messageOf(e)); }
  }

  protected async openCheck() { this.tab.set('check'); try { this.discrepancies.set(await this.api2.serialDiscrepancies()); } catch (e) { this.toasts.error(messageOf(e)); } }

  protected openReceive() { this.rform.patchValue({ serials: '', notes: '' }); this.receiveOpen.set(true); }
  protected async receive() {
    const p = this.product(); if (!p || this.rform.invalid) return;
    const v = this.rform.getRawValue();
    this.busy.set(true);
    try { const r = await this.api2.receiveSerials(p.id, Number(v.warehouseId), v.serials, v.notes.trim() || null); this.toasts.ok(`${r.received} unit(s) registered.`); this.receiveOpen.set(false); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected openTakeBack(s: Serial) { this.tform.patchValue({ reason: '' }); this.takeBack.set(s); }
  protected async doTakeBack() {
    const p = this.product(); const s = this.takeBack(); if (!p || !s) return;
    const v = this.tform.getRawValue();
    this.busy.set(true);
    try { await this.api2.takeBackSerial(p.id, s.serialNumber, Number(v.warehouseId), v.reason.trim() || null); this.toasts.ok('Unit taken back.'); this.takeBack.set(null); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async writeOff(s: Serial) {
    const p = this.product(); if (!p) return;
    const reason = await this.confirm.ask({ title: `Write off ${s.serialNumber}?`, message: 'The unit leaves the register for good.', confirmLabel: 'Write off', danger: true, reason: { label: 'Why is it being written off?', required: true } });
    if (reason === null) return;
    try { await this.api2.writeOffSerial(p.id, s.serialNumber, reason); this.toasts.ok('Unit written off.'); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}

const split = (t: string) => t.split(/[\r\n,;\t]+/).map(x => x.trim()).filter(Boolean);
