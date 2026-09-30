import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { SupplierProduct } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { asNumber } from '../../shared/paged-list';
import { NairaPipe } from '../../shared/ui';

interface Row { id: number; name: string; size: string; unit: string; unitCost: number | string; timesSupplied: number; }

const UNITS = ['Bag', 'Carton', 'Sack', 'Bottle', 'Litre', 'Kg', 'Tonne', 'Piece', 'Pack', 'Roll', 'Drum'];

/**
 * A supplier's own list of goods, typed in as they quote them: their name for it, their pack size, their unit and their usual price.
 * Nothing here is a stock product — this list belongs to the supplier alone and is what the Supplies form picks from.
 */
@Component({
  selector: 'app-supplier-catalog',
  imports: [Icon, Modal, NairaPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-modal [open]="open()" [heading]="'Items ' + supplierName() + ' supplies'" [wide]="true" (closed)="closed.emit()">
      <p class="muted">Type in what this supplier brings you, exactly as they quote it. These are their own items — they are not added to your
        stock or your product list. A supplier can have as many as you like.</p>

      @if (loading()) { <div class="skeleton" style="height:8rem"></div> }
      @else {
        <div class="table-wrap"><table class="table rows">
          <thead><tr><th>Item name</th><th>Pack size</th><th>Counted in</th><th class="num">Their price (₦)</th><th><span class="sr-only">Remove</span></th></tr></thead>
          <tbody>
            @for (r of rows(); track r.id || $index; let i = $index) {
              <tr>
                <td><input class="input" [value]="r.name" placeholder="e.g. Layer Mash" [attr.aria-label]="'Item name, row ' + (i + 1)"
                  (input)="set(i, { name: $any($event.target).value })" /></td>
                <td><input class="input w-size" [value]="r.size" placeholder="20kg" [attr.aria-label]="'Pack size, row ' + (i + 1)"
                  (input)="set(i, { size: $any($event.target).value })" /></td>
                <td><select class="input w-unit" [value]="r.unit" [attr.aria-label]="'Counted in, row ' + (i + 1)" (change)="set(i, { unit: $any($event.target).value })">
                  @for (u of units; track u) { <option [value]="u" [selected]="u === r.unit">{{ u }}</option> }
                </select></td>
                <td class="num"><input class="input num w-cost" type="number" min="0" step="0.01" [value]="r.unitCost"
                  [attr.aria-label]="'Price, row ' + (i + 1)" (input)="set(i, { unitCost: $any($event.target).value })" /></td>
                <td class="actions">
                  <button type="button" class="btn btn-quiet btn-icon" (click)="remove(i)" [attr.aria-label]="'Remove row ' + (i + 1)"><app-icon name="close" [size]="18" /></button>
                  @if (r.timesSupplied > 0) { <div class="muted xs">supplied {{ r.timesSupplied }}×</div> }
                </td>
              </tr>
            }
            @if (!rows().length) { <tr><td colspan="5" class="none">No items yet — add the first one below.</td></tr> }
          </tbody>
        </table></div>

        <div class="foot-row">
          <button type="button" class="btn" (click)="addRow()"><app-icon name="plus" [size]="17" /> Add item</button>
          <span class="muted sm">{{ filled().length }} item(s) · list value {{ listValue() | naira }}</span>
        </div>
        <p class="muted xs">An item that has already been supplied is kept for the record: removing it here just takes it off the pick list.</p>
      }
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      <ng-container modal-actions>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !filled().length" (click)="save()">{{ busy() ? 'Saving…' : 'Save items' }}</button>
      </ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .8125rem; } .xs { font-size: .7rem; }
    .rows td { vertical-align: top; } .rows .input { width: 100%; } .w-size { max-width: 7rem; } .w-unit { max-width: 8rem; } .w-cost { max-width: 9rem; }
    td.actions { white-space: nowrap; text-align: right; }
    .none { text-align: center; color: var(--muted); padding: 1.2rem; }
    .foot-row { display: flex; align-items: center; gap: 1rem; margin-top: .8rem; flex-wrap: wrap; } .foot-row .muted { margin-left: auto; }`,
})
export class SupplierCatalog {
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);

  readonly open = input(false);
  readonly supplierId = input(0);
  readonly supplierName = input('');
  readonly closed = output<void>();
  /** Fires after a successful save so the screen behind can refresh its pick list. */
  readonly saved = output<void>();

  protected readonly units = UNITS;
  protected readonly rows = signal<Row[]>([]);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  /** Rows with a name are the real items; blank rows are just spare typing space. */
  protected readonly filled = computed(() => this.rows().filter(r => r.name.trim().length > 0));
  protected readonly listValue = computed(() => this.filled().reduce((t, r) => t + asNumber(String(r.unitCost)), 0));

  constructor() {
    effect(() => {
      const id = this.supplierId();
      if (!this.open() || !id) return;
      untracked(() => {
        this.rows.set([]); this.error.set(''); this.loading.set(true);
        void this.api2.supplierCatalog(id).then(
          xs => {
            if (this.supplierId() !== id) return;
            this.rows.set(xs.map(this.toRow));
            if (!xs.length) this.addRow();   // a brand-new supplier gets one empty row to type into
          },
          e => this.error.set(messageOf(e)),
        ).finally(() => this.loading.set(false));
      });
    });
  }

  private toRow = (p: SupplierProduct): Row =>
    ({ id: p.id, name: p.name, size: p.size ?? '', unit: p.unit, unitCost: p.unitCost, timesSupplied: p.timesSupplied });

  protected addRow() { this.rows.update(rs => [...rs, { id: 0, name: '', size: '', unit: 'Bag', unitCost: 0, timesSupplied: 0 }]); }
  protected set(i: number, patch: Partial<Row>) { this.rows.update(rs => rs.map((r, j) => (j === i ? { ...r, ...patch } : r))); }
  protected remove(i: number) { this.rows.update(rs => rs.filter((_, j) => j !== i)); }

  protected async save() {
    const id = this.supplierId(); if (!id) return;
    const items = this.filled().map(r => ({
      id: r.id, name: r.name.trim(), size: r.size.trim() || null, unit: r.unit, unitCost: Math.max(0, asNumber(String(r.unitCost))), isActive: true,
    }));
    this.busy.set(true); this.error.set('');
    try {
      const saved = await this.api2.saveSupplierCatalog(id, items);
      this.rows.set(saved.map(this.toRow));
      this.toasts.ok(`${this.supplierName()}: ${saved.length} item(s) saved.`);
      this.saved.emit(); this.closed.emit();
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
