import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Customer, CustomerInput, CustomerType } from '../../core/models';
import { Icon } from '../../shared/icon';
import { CustomerMetricsDialog } from './customer-metrics';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { NairaPipe, Pager, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-customers',
  imports: [ReactiveFormsModule, Icon, CustomerMetricsDialog, Modal, NairaPipe, Pager, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Customers</h1>
        <div class="actions">
          <a class="btn" href="/api/exports/customers" download><app-icon name="download" [size]="18" /> Excel</a><button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> New customer</button></div>
      </div>
      <section class="card">
        <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
          <input class="input" type="search" placeholder="Search name, phone or location" aria-label="Search customers" (input)="list.setSearch($any($event.target).value)" /></div></div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Customer</th><th>Type</th><th>Last 12 months</th><th class="num">Owes</th><th class="num">Rebate rate</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (c of list.items(); track c.id) {
              <tr><td><span class="strong">{{ c.name }}</span><div class="muted sm">{{ c.phone }}{{ c.location ? ' · ' + c.location : '' }}</div></td><td>{{ c.customerType }}</td>
                <td><span class="mono">{{ c.trailingTwelveMonthSpend | naira }}</span> <app-stamp [label]="c.ranking" /></td>
                <td class="num mono" [class.owes]="c.balance > 0">{{ c.balance | naira }}</td><td class="num mono">{{ c.rebateRatePct }}%</td>
                <td class="actions">
                  @if (c.balance > 0) { <button type="button" class="btn btn-sm btn-primary" (click)="openPay(c)">Record payment</button> }
                  <button type="button" class="btn btn-sm" (click)="metricsFor.set(c.id)">History</button>
                  <button type="button" class="btn btn-sm" (click)="openEdit(c)">Edit</button>
                  <button type="button" class="btn btn-sm btn-danger" (click)="remove(c)">Delete</button></td></tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No customers found</strong>Add a customer to sell on credit or track their spend.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <app-customer-metrics [customerId]="metricsFor()" (closed)="metricsFor.set(null)" />

    <app-modal [open]="formOpen()" [heading]="editing() ? 'Edit customer' : 'New customer'" [wide]="true" (closed)="formOpen.set(false)">
      <form id="cf" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
        <div class="field span-2"><label for="c-n">Name</label><input id="c-n" class="input" formControlName="name" /></div>
        <div class="field"><label for="c-t">Customer type</label><select id="c-t" class="input" formControlName="customerType"><option>Retailer</option><option>Wholesaler</option><option>Distributor</option><option>Walk-in</option></select></div>
        <div class="field"><label for="c-c">Contact person</label><input id="c-c" class="input" formControlName="contactName" /></div>
        <div class="field"><label for="c-p">Phone</label><input id="c-p" class="input" inputmode="tel" formControlName="phone" /></div>
        <div class="field"><label for="c-e">Email</label><input id="c-e" class="input" type="email" formControlName="email" /></div>
        <div class="field"><label for="c-l">Location</label><input id="c-l" class="input" formControlName="location" placeholder="Lagos, Lagos State" /></div>
        <div class="field"><label for="c-x">Tax ID</label><input id="c-x" class="input" formControlName="taxId" /></div>
        <div class="field span-2"><label for="c-a">Address</label><input id="c-a" class="input" formControlName="address" /></div>
        <div class="field"><label for="c-r">Rebate rate (% of net sales)</label><input id="c-r" class="input num" type="number" min="0" max="100" step="0.1" formControlName="rebateRatePct" /></div>
        <div class="field"><label for="c-cl">Credit limit</label><input id="c-cl" class="input num" type="number" min="0" formControlName="creditLimit" /></div>
      </form>
      <ng-container modal-actions><button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button><button type="submit" form="cf" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Add customer' }}</button></ng-container>
    </app-modal>

    <app-modal [open]="!!paying()" heading="Record payment" (closed)="paying.set(null)">
      @if (paying(); as c) {
        <p><strong>{{ c.name }}</strong> owes <strong class="mono">{{ c.balance | naira }}</strong>. The payment clears their oldest unpaid invoices first.</p>
        <form id="pay" [formGroup]="payForm" (ngSubmit)="pay()" class="form-grid" style="margin-top:.9rem" novalidate>
          <div class="field"><label for="pa">Amount received</label><input id="pa" class="input num" type="number" min="0.01" [max]="c.balance" step="0.01" formControlName="amount" /></div>
          <div class="field"><label for="pm">Paid by</label><select id="pm" class="input" formControlName="method"><option>Cash</option><option>Bank Transfer</option><option>Card</option></select></div>
        </form>
      }
      <ng-container modal-actions><button type="button" class="btn" (click)="paying.set(null)">Cancel</button><button type="submit" form="pay" class="btn btn-primary" [disabled]="payForm.invalid || busy()">Record payment</button></ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; } .owes { color: var(--stamp); font-weight: 600; }`,
})
export class CustomersPage implements OnInit {
  private readonly api = inject(Api);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly list = new PagedList<Customer>(q => this.api.customers(q));
  protected readonly formOpen = signal(false);
  protected readonly metricsFor = signal<number | null>(null);
  protected readonly editing = signal<Customer | null>(null);
  protected readonly paying = signal<Customer | null>(null);
  protected readonly busy = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(150)]], customerType: ['Retailer' as CustomerType], contactName: [''], phone: [''], email: ['', Validators.email],
    location: [''], taxId: [''], address: [''], rebateRatePct: [1, [Validators.min(0), Validators.max(100)]], creditLimit: [0, Validators.min(0)],
  });
  protected readonly payForm = this.fb.nonNullable.group({ amount: [0, [Validators.required, Validators.min(0.01)]], method: ['Cash'] });

  ngOnInit() { void this.list.load(); }

  protected openNew() { this.editing.set(null); this.form.reset({ customerType: 'Retailer', rebateRatePct: 1, creditLimit: 0 }); this.formOpen.set(true); }
  protected openEdit(c: Customer) {
    this.editing.set(c);
    this.form.reset({ name: c.name, customerType: c.customerType, contactName: c.contactName ?? '', phone: c.phone ?? '', email: c.email ?? '', location: c.location ?? '',
      taxId: c.taxId ?? '', address: c.address ?? '', rebateRatePct: c.rebateRatePct, creditLimit: c.creditLimit });
    this.formOpen.set(true);
  }
  protected openPay(c: Customer) { this.paying.set(c); this.payForm.reset({ amount: c.balance, method: 'Cash' }); }

  protected async save() {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    const n = (s: string) => s.trim() || null;
    const input: CustomerInput = { name: v.name.trim(), customerType: v.customerType, contactName: n(v.contactName), phone: n(v.phone), email: n(v.email), location: n(v.location),
      taxId: n(v.taxId), address: n(v.address), rebateRatePct: v.rebateRatePct, creditLimit: v.creditLimit };
    this.busy.set(true);
    try {
      const e = this.editing();
      if (e) await this.api.updateCustomer(e.id, input); else await this.api.createCustomer(input);
      this.toasts.ok(e ? 'Customer updated.' : 'Customer added.');
      this.formOpen.set(false); await this.list.load();
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async pay() {
    const c = this.paying(); if (!c || this.payForm.invalid) return;
    const v = this.payForm.getRawValue();
    this.busy.set(true);
    try { await this.api.recordPayment(c.id, v.amount, v.method); this.toasts.ok('Payment recorded.'); this.paying.set(null); await this.list.load(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async remove(c: Customer) {
    const ok = await this.confirm.ask({ title: `Delete ${c.name}?`, message: 'Customers with sales history can’t be deleted. This can’t be undone.', confirmLabel: 'Delete customer', danger: true });
    if (ok === null) return;
    try { await this.api.deleteCustomer(c.id); this.toasts.ok('Customer deleted.'); await this.list.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
