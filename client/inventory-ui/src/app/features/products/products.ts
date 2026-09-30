import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { CatalogDialog, ChosenItem } from './catalog-dialog';
import { Auth } from '../../core/auth.service';
import { Category, Product, ProductInput, Supplier, Warehouse } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { NairaPipe, Pager } from '../../shared/ui';

/** How a product is packed and sold. The value is what's stored and printed next to quantities ("12 Ctn in stock"). */
const UNITS = [
  { value: 'Bag', label: 'Bags' },
  { value: 'Ctn', label: 'Cartons (Ctns)' },
  { value: 'Pack', label: 'Packs' },
  { value: 'Pcs', label: 'Pieces (Pcs)' },
];

@Component({
  selector: 'app-products',
  imports: [ReactiveFormsModule, Icon, Modal, NairaPipe, Pager, CatalogDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Products</h1>
        <div class="actions">
          <a class="btn" href="/api/exports/inventory" download><app-icon name="download" [size]="18" /> Excel</a>
          <button type="button" class="btn" (click)="catalogOpen.set(true)"><app-icon name="file" [size]="18" /> Price list / catalog{{ picked().length ? ' (' + picked().length + ')' : '' }}</button>@if (auth.isAdmin()) { <button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> New product</button> }</div>
      </div>

      <section class="card">
        <div class="toolbar">
          <div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search name, SKU or barcode" aria-label="Search products" (input)="list.setSearch($any($event.target).value)" /></div>
          @if (auth.isAdmin()) { <label class="check"><input type="checkbox" (change)="inactive = $any($event.target).checked; list.goTo(1)" /> Show inactive</label> }
        </div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th class="tick"><span class="sr-only">Choose for the catalog</span></th><th>Product</th><th>Category</th><th class="num">In stock</th><th class="num">Retail</th><th class="num">Wholesale</th><th class="num">Distributor</th>@if (auth.isAdmin()) { <th class="num">Cost</th> }<th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (p of list.items(); track p.id) {
              <tr [class.inactive]="!p.isActive">
                <td class="tick"><input type="checkbox" [checked]="isPicked(p.id)" (change)="pick(p, $any($event.target).checked)" [attr.aria-label]="'Choose ' + p.name + ' for the catalog'" /></td>
                <td><span class="strong">{{ p.name }}</span> @if (!p.isActive) { <span class="stamp stamp-info">Switched off</span> }<div class="muted mono sm">{{ p.sku }} · {{ p.barcode }}</div></td>
                <td>{{ p.category }}</td>
                <td class="num mono" [class.low]="p.totalQuantity <= p.reorderLevel">{{ p.totalQuantity }} <span class="muted">{{ p.unit }}</span></td>
                <td class="num mono">{{ p.priceRetail | naira }}</td><td class="num mono">{{ p.priceWholesaler | naira }}</td><td class="num mono">{{ p.priceDistributor | naira }}</td>
                @if (auth.isAdmin()) { <td class="num mono muted">{{ p.costPrice | naira }}</td> }
                <td class="actions">
                  @if (auth.isAdmin()) {
                    <a class="btn btn-sm" [href]="'/api/products/' + p.id + '/label.pdf'" target="_blank" rel="noopener">Label</a>
                    <button type="button" class="btn btn-sm" (click)="openEdit(p)">Edit</button>
                    @if (auth.canDelete()) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(p)">Delete</button> }
                  }
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No products found</strong>Add a product to start tracking its stock.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <app-modal [open]="dialogOpen()" [heading]="editing() ? 'Edit product' : 'New product'" [wide]="true" (closed)="dialogOpen.set(false)">
      <form id="pf" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
        <div class="field"><label for="f-sku">SKU</label><input id="f-sku" class="input mono" formControlName="sku" /></div>
        <div class="field"><label for="f-name">Name</label><input id="f-name" class="input" formControlName="name" /></div>
        <div class="field"><label for="f-cat">Category</label>
          <div class="inline"><select id="f-cat" class="input" formControlName="categoryId">@for (c of categories(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select>
            @if (auth.isAdmin()) { <button type="button" class="btn btn-sm" (click)="addCategory()">Add</button> }</div>
          @if (!categories().length) { <span class="hint">No categories yet. Click <strong>Add</strong> to create one, e.g. “Dog food” or “Cat food”.</span> }</div>
        <div class="field"><label for="f-unit">Sold in</label>
          <select id="f-unit" class="input" formControlName="unit">
            @for (u of units(); track u.value) { <option [value]="u.value">{{ u.label }}</option> }
          </select><span class="hint">How one unit of stock is counted and sold.</span></div>
        <div class="field"><label for="f-r">Retail price</label><input id="f-r" class="input num" type="number" min="0" step="0.01" formControlName="priceRetail" /></div>
        <div class="field"><label for="f-w">Wholesale price</label><input id="f-w" class="input num" type="number" min="0" step="0.01" formControlName="priceWholesaler" /></div>
        <div class="field"><label for="f-d">Distributor price</label><input id="f-d" class="input num" type="number" min="0" step="0.01" formControlName="priceDistributor" /></div>
        @if (auth.isAdmin()) { <div class="field"><label for="f-c">Cost price</label><input id="f-c" class="input num" type="number" min="0" step="0.01" formControlName="costPrice" /></div> }
        <div class="field"><label for="f-ro">Reorder when stock reaches</label><input id="f-ro" class="input num" type="number" min="0" formControlName="reorderLevel" /></div>
        <div class="field"><label for="f-bc">Barcode</label><input id="f-bc" class="input mono" formControlName="barcode" /><span class="hint">Leave blank and one is made for you.</span></div>

        @if (auth.isAdmin() && suppliers().length) {
          <div class="field span-2"><span class="label">Supplied by</span>
            <div class="sup">
              @for (s of suppliers(); track s.id) {
                <label class="chip-pick" [class.on]="supplierIds().includes(s.id)"><input type="checkbox" [checked]="supplierIds().includes(s.id)" (change)="toggleSupplier(s.id, $any($event.target).checked)" /> {{ s.name }}</label>
              }
            </div><span class="hint">Tick who supplies this. It appears on their item list, ready to pick when you record a purchase from them.</span></div>
        }
        @if (editing(); as e) {
          <div class="field span-2 photo"><span class="label">Photo (shown on the catalog)</span>
            <div class="photo-row">
              @if (!photoMissing()) { <img [src]="'/api/products/' + e.id + '/image?v=' + photoStamp()" alt="" (error)="photoMissing.set(true)" (load)="photoMissing.set(false)" /> }
              @else { <span class="no-photo">No photo yet</span> }
              <label class="btn btn-sm">{{ photoMissing() ? 'Add photo' : 'Change photo' }}<input type="file" accept="image/png,image/jpeg" hidden (change)="upload(e, $any($event.target))" /></label>
              @if (!photoMissing() && auth.canDelete()) { <button type="button" class="btn btn-sm btn-danger" (click)="removePhoto(e)">Remove</button> }
            </div><span class="hint">PNG or JPEG, up to 2 MB. A square photo on a plain background looks best.</span></div>
        }
        @if (!editing()) {
          <fieldset class="span-2 open"><legend class="label">Stock already on the shelf (optional)</legend>
            <div class="form-grid">
              <div class="field"><label for="o-q">Quantity</label><input id="o-q" class="input num" type="number" min="0" formControlName="openingQuantity" /></div>
              <div class="field"><label for="o-w">Warehouse</label><select id="o-w" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
              <div class="field"><label for="o-b">Batch number</label><input id="o-b" class="input" formControlName="batchNumber" /></div>
              <div class="field"><label for="o-e">Best before</label><input id="o-e" class="input" type="date" formControlName="expiryDate" /></div>
            </div></fieldset>
        }
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="dialogOpen.set(false)">Cancel</button>
        <button type="submit" form="pf" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Add product' }}</button>
      </ng-container>
    </app-modal>

    <app-catalog-dialog [open]="catalogOpen()" [preselected]="picked()" (closed)="catalogOpen.set(false)" />`,
  styles: `.sm { font-size: .75rem; } th.tick, td.tick { width: 2rem; padding-right: 0; }
    .sup { display: flex; flex-wrap: wrap; gap: .4rem; margin-top: .3rem; max-height: 8.5rem; overflow: auto; }
    .chip-pick { display: inline-flex; align-items: center; gap: .35rem; padding: .3rem .6rem; border: 1px solid var(--line-strong); border-radius: 999px; font-size: .8125rem; cursor: pointer; }
    .chip-pick.on { background: var(--brand-tint); border-color: var(--brand); }
    .photo-row { display: flex; align-items: center; gap: .7rem; margin-top: .3rem; } .photo-row img { width: 72px; height: 72px; object-fit: contain; border: 1px solid var(--line); border-radius: var(--r-2); background: #fff; }
    .no-photo { width: 72px; height: 72px; display: grid; place-items: center; text-align: center; font-size: .7rem; color: var(--muted); border: 1px dashed var(--line-strong); border-radius: var(--r-2); } .low { color: var(--stamp); font-weight: 600; } tr.inactive { opacity: .6; } .inline { display: flex; gap: .4rem; } fieldset.open { border: 1px dashed var(--line-strong); border-radius: var(--r-2); padding: .8rem; margin: 0; }`,
})
export class ProductsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly auth = inject(Auth);

  protected inactive = false;

  // ---- catalog: products ticked here (kept across pages and searches)
  protected readonly catalogOpen = signal(false);
  protected readonly picked = signal<ChosenItem[]>([]);
  protected isPicked(id: number) { return this.picked().some(x => x.id === id); }
  protected pick(p: Product, on: boolean) { this.picked.update(xs => (on ? (xs.some(x => x.id === p.id) ? xs : [...xs, { id: p.id, name: p.name }]) : xs.filter(x => x.id !== p.id))); }

  // ---- product photo
  protected readonly photoMissing = signal(true);
  protected readonly photoStamp = signal(Date.now());
  protected async upload(p: Product, el: HTMLInputElement) {
    const f = el.files?.[0]; el.value = ''; if (!f) return;
    if (f.size > 2 * 1024 * 1024) { this.toasts.error('That image is larger than 2 MB. Use a smaller file.'); return; }
    try { await this.api2.uploadProductImage(p.id, f); this.photoMissing.set(false); this.photoStamp.set(Date.now()); this.toasts.ok('Photo saved.'); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async removePhoto(p: Product) {
    try { await this.api2.removeProductImage(p.id); this.photoMissing.set(true); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected readonly list = new PagedList<Product>(q => this.api.products({ ...q, includeInactive: this.inactive || undefined }));
  protected readonly categories = signal<Category[]>([]);
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly dialogOpen = signal(false);
  protected readonly editing = signal<Product | null>(null);
  protected readonly busy = signal(false);
  /** The packaging choices. A product saved earlier with some other unit keeps it (it's added to the list while editing it). */
  protected readonly units = computed(() => {
    const base = UNITS;
    const current = this.editing()?.unit;
    return current && !base.some(u => u.value === current) ? [...base, { value: current, label: current }] : base;
  });

  protected readonly form = this.fb.nonNullable.group({
    sku: ['', [Validators.required, Validators.maxLength(30)]], name: ['', [Validators.required, Validators.maxLength(150)]],
    categoryId: [0, Validators.min(1)], unit: ['Bag', Validators.required], reorderLevel: [0, Validators.min(0)],
    costPrice: [0, Validators.min(0)], priceRetail: [0, Validators.min(0)], priceWholesaler: [0, Validators.min(0)], priceDistributor: [0, Validators.min(0)],
    barcode: [''], openingQuantity: [0, Validators.min(0)], warehouseId: [0], batchNumber: [''], expiryDate: [''],
  });

  ngOnInit() {
    void this.list.load();
    void this.api.categories().then(c => this.categories.set(c));
    void this.api.warehouses().then(w => this.warehouses.set(w));
    if (this.auth.isAdmin()) void this.api.suppliers({ pageSize: 200 }).then(s => this.suppliers.set(s.items), () => { /* the picker just isn't shown */ });
  }

  // ---- who supplies the product (puts it on those suppliers' item lists)
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly supplierIds = signal<number[]>([]);
  /** False while an edited product's current suppliers couldn't be read: saving then leaves its links untouched. */
  private suppliersKnown = true;
  protected toggleSupplier(id: number, on: boolean) { this.supplierIds.update(xs => (on ? [...new Set([...xs, id])] : xs.filter(x => x !== id))); }

  protected openNew() {
    this.supplierIds.set([]); this.suppliersKnown = true;
    this.editing.set(null);
    this.form.reset({ sku: '', name: '', categoryId: this.categories()[0]?.id ?? 0, unit: 'Bag', reorderLevel: 0, costPrice: 0, priceRetail: 0, priceWholesaler: 0, priceDistributor: 0,
      barcode: '', openingQuantity: 0, warehouseId: this.warehouses()[0]?.id ?? 0, batchNumber: '', expiryDate: '' });
    this.dialogOpen.set(true);
  }

  protected openEdit(p: Product) {
    this.editing.set(p); this.photoMissing.set(false); this.photoStamp.set(Date.now());
    this.supplierIds.set([]); this.suppliersKnown = false;
    if (this.auth.isAdmin()) void this.api2.productSuppliers(p.id).then(xs => {
      if (this.editing()?.id === p.id) { this.supplierIds.set(xs.map(x => x.supplierId)); this.suppliersKnown = true; }
    }, () => { /* links stay as they are */ });
    this.form.patchValue({ sku: p.sku, name: p.name, categoryId: p.categoryId, unit: p.unit, reorderLevel: p.reorderLevel, costPrice: p.costPrice ?? 0,
      priceRetail: p.priceRetail, priceWholesaler: p.priceWholesaler, priceDistributor: p.priceDistributor, barcode: p.barcode ?? '' });
    this.dialogOpen.set(true);
  }

  protected async addCategory() {
    const name = window.prompt('Name for the new category');
    if (!name?.trim()) return;
    try {
      const { id } = await this.api.addCategory(name.trim());
      this.categories.set(await this.api.categories());
      this.form.controls.categoryId.setValue(id);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async save() {
    if (this.form.invalid || this.busy()) return;
    const v = this.form.getRawValue();
    const product: ProductInput = {
      sku: v.sku.trim(), name: v.name.trim(), categoryId: v.categoryId, unit: v.unit, reorderLevel: v.reorderLevel, costPrice: v.costPrice,
      priceDistributor: v.priceDistributor, priceWholesaler: v.priceWholesaler, priceRetail: v.priceRetail, barcode: v.barcode.trim() || null,
      tracksSerial: this.editing()?.tracksSerial ?? false,
      supplierIds: this.auth.isAdmin() && this.suppliersKnown && this.suppliers().length ? this.supplierIds() : null,
    };
    this.busy.set(true);
    try {
      const e = this.editing();
      if (e) {
        // Clerks never see cost, so an admin-only field must not be overwritten with a blank.
        await this.api.updateProduct(e.id, product);
        this.toasts.ok('Product updated.');
      } else {
        await this.api.createProduct({ product, openingQuantity: v.openingQuantity, warehouseId: v.warehouseId, batchNumber: v.batchNumber || null, expiryDate: v.expiryDate || null });
        this.toasts.ok('Product added.');
      }
      this.dialogOpen.set(false);
      await this.list.load();
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async remove(p: Product) {
    const ok = await this.confirm.ask({
      title: `Delete ${p.name} permanently?`,
      message: (p.totalQuantity > 0 ? `Its ${p.totalQuantity} unit(s) in stock, ` : 'Its ') +
        'batches, stock movements, serial numbers and price history are deleted with it. This can’t be undone. ' +
        'A product that is on a sale, purchase or quotation can’t be deleted until those are.',
      confirmLabel: 'Delete permanently', danger: true,
    });
    if (ok === null) return;
    try {
      await this.api.deleteProduct(p.id);
      this.toasts.ok(`${p.name} deleted.`);
      await this.list.load();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
