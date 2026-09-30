import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Auth } from '../../core/auth.service';
import { RouterLink } from '@angular/router';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Supplier, SupplierInput } from '../../core/models';
import { SupplierCatalog } from './supplier-catalog';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { NairaPipe, Pager } from '../../shared/ui';

@Component({
  selector: 'app-suppliers',
  imports: [ReactiveFormsModule, RouterLink, Icon, Modal, SupplierCatalog, NairaPipe, Pager],
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
      @if (editing(); as e) {
        <p class="items-line"><span class="muted">What they supply — the list you pick from when recording a supply or a purchase.</span>
          <button type="button" class="btn btn-sm" (click)="openItems(e)"><app-icon name="plus" [size]="15" /> Manage items</button></p>
      } @else {
        <p class="items-line"><span class="muted">Once you save, you’ll be asked which products this supplier brings you.</span></p>
      }
      <ng-container modal-actions><button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button><button type="submit" form="sf" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Add supplier' }}</button></ng-container>
    </app-modal>

    <app-supplier-catalog [open]="!!itemsFor()" [supplierId]="itemsFor()?.id ?? 0" [supplierName]="itemsFor()?.name ?? ''"
      (closed)="itemsFor.set(null)" (saved)="list.load()" />`,
  styles: `.sm { font-size: .75rem; } .owes { color: var(--stamp); font-weight: 600; } td.actions { white-space: nowrap; }
    .items-line { display: flex; align-items: center; gap: .8rem; flex-wrap: wrap; margin: 1rem 0 0; padding-top: .9rem; border-top: 1px dashed var(--line-strong); }
    .items-line .muted { font-size: .8125rem; flex: 1; } .items-line .btn { margin-left: auto; }`,
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

  // ---- the goods this supplier sells us (the editor is shared with the supplier page and the Supplies screen)
  protected readonly itemsFor = signal<Supplier | null>(null);
  protected openItems(s: Supplier) { this.itemsFor.set(s); }

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
      if (e) {
        await this.api.updateSupplier(e.id, input);
        this.toasts.ok('Supplier updated.'); this.formOpen.set(false); await this.list.load();
      } else {
        // A new supplier is only useful once we know what they bring, so go straight on to their item list.
        const { id } = await this.api.createSupplier(input);
        this.toasts.ok(`${input.name} added. Now add the items they supply.`);
        this.formOpen.set(false);
        await this.list.load();
        this.itemsFor.set({ ...input, id, balance: 0 } as Supplier);
      }
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async remove(s: Supplier) {
    const ok = await this.confirm.ask({ title: `Delete ${s.name}?`, message: 'Suppliers with purchase orders can’t be deleted.', confirmLabel: 'Delete supplier', danger: true });
    if (ok === null) return;
    try { await this.api.deleteSupplier(s.id); this.toasts.ok('Supplier deleted.'); await this.list.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
