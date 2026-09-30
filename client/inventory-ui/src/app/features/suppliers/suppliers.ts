import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Auth } from '../../core/auth.service';
import { RouterLink } from '@angular/router';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Product, Supplier, SupplierInput } from '../../core/models';
import { SupplierItem } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { NairaPipe, Pager } from '../../shared/ui';

@Component({
  selector: 'app-suppliers',
  imports: [ReactiveFormsModule, RouterLink, Icon, Modal, NairaPipe, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Suppliers</h1>
        <div class="actions"><button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> New supplier</button></div>
      </div>
      <section class="card">
        <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
          <input class="input" type="search" placeholder="Search name or phone" aria-label="Search suppliers" (input)="list.setSearch($any($event.target).value)" /></div>
          <span class="muted">We owe suppliers <strong class="mono">{{ owedTotal() | naira }}</strong> on this page</span></div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Supplier</th><th>Supplies</th><th>Contact</th><th class="num">We owe</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (s of list.items(); track s.id) {
              <tr><td><a class="strong" [routerLink]="['/suppliers', s.id]">{{ s.name }}</a></td><td>{{ s.category ?? '—' }}</td><td>{{ s.contactName ?? '' }}<div class="muted sm">{{ s.phone }}</div></td>
                <td class="num mono" [class.owes]="s.balance > 0">{{ s.balance | naira }}</td>
                <td class="actions"><a class="btn btn-sm btn-primary" [routerLink]="['/suppliers', s.id]">Open</a> <button type="button" class="btn btn-sm" (click)="openItems(s)">Items</button> <button type="button" class="btn btn-sm" (click)="openEdit(s)">Edit</button> @if (auth.canDelete()) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(s)">Delete</button> }</td></tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No suppliers found</strong>Add the businesses you buy from.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <app-modal [open]="formOpen()" [heading]="editing() ? 'Edit supplier' : 'New supplier'" [wide]="true" (closed)="formOpen.set(false)">
      <form id="sf" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
        <div class="field span-2"><label for="s-n">Name</label><input id="s-n" class="input" formControlName="name" /></div>
        <div class="field"><label for="s-c">What they supply</label><input id="s-c" class="input" formControlName="category" /></div>
        <div class="field"><label for="s-k">Contact person</label><input id="s-k" class="input" formControlName="contactName" /></div>
        <div class="field"><label for="s-p">Phone</label><input id="s-p" class="input" inputmode="tel" formControlName="phone" /></div>
        <div class="field"><label for="s-e">Email</label><input id="s-e" class="input" type="email" formControlName="email" /></div>
        <div class="field"><label for="s-t">Tax ID</label><input id="s-t" class="input" formControlName="taxId" /></div>
        <div class="field"><label for="s-a">Address</label><input id="s-a" class="input" formControlName="address" /></div>
      </form>
      <ng-container modal-actions><button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button><button type="submit" form="sf" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Add supplier' }}</button></ng-container>
    </app-modal>

    <app-modal [open]="!!itemsFor()" [heading]="'What we buy from ' + (itemsFor()?.name ?? '')" [wide]="true" (closed)="itemsFor.set(null)">
      <p class="muted">Add the goods you buy from this supplier and what they usually charge. A new purchase from them lists these, so you only type the quantities.</p>
      <div class="search pick-search"><app-icon name="search" [size]="17" />
        <input #iq class="input" placeholder="Search products to add" aria-label="Search products to add" (input)="searchItems(iq.value)" autocomplete="off" /></div>
      @if (itemResults().length) {
        <ul class="pick">@for (p of itemResults(); track p.id) { <li><button type="button" (click)="addItem(p); iq.value = ''; itemResults.set([])"><span><strong>{{ p.name }}</strong> <span class="muted mono">{{ p.sku }}</span></span><span class="muted mono">cost {{ p.costPrice | naira }}</span></button></li> }</ul>
      }
      @if (items().length) {
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Product</th><th>Unit</th><th class="num">Usual cost (₦)</th><th><span class="sr-only">Remove</span></th></tr></thead>
          <tbody>@for (it of items(); track it.productId; let i = $index) {
            <tr><td><span class="strong">{{ it.product }}</span><div class="muted mono sm">{{ it.sku }}</div></td><td>{{ it.unit }}</td>
              <td class="num"><input class="input num w-cost" type="number" min="0" step="0.01" [value]="it.unitCost" [attr.aria-label]="'Usual cost of ' + it.product" (input)="setCost(i, $any($event.target).value)" /></td>
              <td class="actions"><button type="button" class="btn btn-quiet btn-icon" (click)="removeItem(i)" [attr.aria-label]="'Remove ' + it.product"><app-icon name="close" [size]="18" /></button></td></tr>
          }</tbody>
        </table></div>
      } @else { <div class="empty"><strong>No items yet</strong>Search above to add what you buy from them.</div> }
      <ng-container modal-actions><button type="button" class="btn" (click)="itemsFor.set(null)">Cancel</button><button type="button" class="btn btn-primary" [disabled]="busy()" (click)="saveItems()">Save list</button></ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; } .owes { color: var(--stamp); font-weight: 600; } td.actions { white-space: nowrap; }
    .pick-search { margin: .8rem 0 .4rem; } .w-cost { width: 8rem; }
    .pick { list-style: none; margin: 0 0 .6rem; padding: 0; border: 1px solid var(--line); border-radius: var(--r-2); max-height: 14rem; overflow: auto; background: #fff; }
    .pick button { display: flex; justify-content: space-between; gap: 1rem; width: 100%; padding: .55rem .9rem; background: none; border: 0; border-bottom: 1px solid var(--line); text-align: left; cursor: pointer; }
    .pick button:hover, .pick button:focus-visible { background: var(--brand-tint); }`,
})
export class SuppliersPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly list = new PagedList<Supplier>(q => this.api.suppliers(q));
  protected readonly formOpen = signal(false);
  protected readonly editing = signal<Supplier | null>(null);
  protected readonly busy = signal(false);
  protected readonly owedTotal = () => this.list.items().reduce((s, x) => s + Math.max(0, x.balance), 0);

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(150)]], category: [''], contactName: [''], phone: [''], email: ['', Validators.email], taxId: [''], address: [''],
  });

  ngOnInit() { void this.list.load(); }

  // ---- the goods this supplier sells us
  protected readonly itemsFor = signal<Supplier | null>(null);
  protected readonly items = signal<SupplierItem[]>([]);
  protected readonly itemResults = signal<Product[]>([]);
  private itemTimer: ReturnType<typeof setTimeout> | null = null;

  protected async openItems(s: Supplier) {
    this.items.set([]); this.itemResults.set([]); this.itemsFor.set(s);
    try { this.items.set(await this.api2.supplierItems(s.id)); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected searchItems(term: string) {
    if (this.itemTimer) clearTimeout(this.itemTimer);
    const t = term.trim(); if (t.length < 2) { this.itemResults.set([]); return; }
    this.itemTimer = setTimeout(async () => { try { this.itemResults.set((await this.api.products({ search: t, pageSize: 8 })).items); } catch { this.itemResults.set([]); } }, 220);
  }
  protected addItem(p: Product) {
    this.items.update(xs => xs.some(x => x.productId === p.id) ? xs
      : [...xs, { id: 0, productId: p.id, product: p.name, sku: p.sku, unit: p.unit, unitCost: p.costPrice ?? 0, isActive: p.isActive }]);
  }
  protected setCost(i: number, v: string) { const n = Math.max(0, Number(v) || 0); this.items.update(xs => xs.map((x, j) => (j === i ? { ...x, unitCost: n } : x))); }
  protected removeItem(i: number) { this.items.update(xs => xs.filter((_, j) => j !== i)); }
  protected async saveItems() {
    const s = this.itemsFor(); if (!s) return;
    this.busy.set(true);
    try {
      await this.api2.setSupplierItems(s.id, this.items().map(x => ({ productId: x.productId, unitCost: x.unitCost })));
      this.toasts.ok(`${s.name}: ${this.items().length} item(s) saved.`); this.itemsFor.set(null);
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }
  protected openNew() { this.editing.set(null); this.form.reset(); this.formOpen.set(true); }
  protected openEdit(s: Supplier) {
    this.editing.set(s);
    this.form.reset({ name: s.name, category: s.category ?? '', contactName: s.contactName ?? '', phone: s.phone ?? '', email: s.email ?? '', taxId: s.taxId ?? '', address: s.address ?? '' });
    this.formOpen.set(true);
  }

  protected async save() {
    if (this.form.invalid) return;
    const v = this.form.getRawValue(); const n = (s: string) => s.trim() || null;
    const input: SupplierInput = { name: v.name.trim(), category: n(v.category), contactName: n(v.contactName), phone: n(v.phone), email: n(v.email), taxId: n(v.taxId), address: n(v.address) };
    this.busy.set(true);
    try {
      const e = this.editing();
      if (e) await this.api.updateSupplier(e.id, input); else await this.api.createSupplier(input);
      this.toasts.ok(e ? 'Supplier updated.' : 'Supplier added.'); this.formOpen.set(false); await this.list.load();
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async remove(s: Supplier) {
    const ok = await this.confirm.ask({ title: `Delete ${s.name}?`, message: 'Suppliers with purchase orders can’t be deleted.', confirmLabel: 'Delete supplier', danger: true });
    if (ok === null) return;
    try { await this.api.deleteSupplier(s.id); this.toasts.ok('Supplier deleted.'); await this.list.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
