import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, messageOf, problemOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Auth } from '../../core/auth.service';
import { Warehouse } from '../../core/models';
import { QuotationRow } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { SendEmail } from './send-email';
import { PagedList } from '../../shared/paged-list';
import { DayPipe, NairaPipe, Pager, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-quotations',
  imports: [ReactiveFormsModule, RouterLink, Icon, Modal, NairaPipe, DayPipe, Pager, Stamp, SendEmail],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Quotations</h1>
        <div class="actions"><a class="btn btn-primary" routerLink="/quotations/new"><app-icon name="plus" [size]="18" /> New quotation</a></div>
      </div>
      <p class="muted lede">A quotation prices a job without touching stock or anyone’s balance. Turn it into a sale when the customer says yes.</p>
      <section class="card">
        <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
          <input class="input" type="search" placeholder="Search number, customer or status" aria-label="Search quotations" (input)="list.setSearch($any($event.target).value)" /></div></div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Quotation</th><th>Customer</th><th>Date</th><th class="num">Total</th><th>Status</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (q of list.items(); track q.id) {
              <tr>
                <td><span class="docno">{{ q.quotationNumber }}</span><div class="muted sm">{{ q.preparedBy }}</div></td>
                <td class="strong">{{ q.customer }}</td><td>{{ q.quotationDate | day }}</td><td class="num mono">{{ q.totalAmount | naira }}</td>
                <td><app-stamp [label]="q.status" /></td>
                <td class="actions">
                  <a class="btn btn-sm" [href]="'/api/quotations/' + q.id + '/pdf'" target="_blank" rel="noopener"><app-icon name="print" [size]="15" /> PDF</a>
                  <button type="button" class="btn btn-sm" (click)="emailing.set(q)">Email</button>
                  @if (q.status === 'Open') {
                    <button type="button" class="btn btn-sm btn-primary" (click)="openConvert(q)">Make a sale</button>
                    @if (auth.isAdmin()) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(q)">Delete</button> }
                  } @else if (q.convertedInvoiceId) { <a class="btn btn-sm" [routerLink]="['/sales', q.convertedInvoiceId]">View sale</a> }
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No quotations yet</strong>Price a job for a customer and it will be listed here.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <app-send-email [open]="!!emailing()" mode="quotation" [quotationId]="emailing()?.id ?? null" (closed)="emailing.set(null)" />

    <app-modal [open]="!!converting()" heading="Turn into a sale" (closed)="converting.set(null)">
      @if (converting(); as q) {
        <p>{{ q.quotationNumber }} for <strong>{{ q.customer }}</strong> — <span class="mono">{{ q.totalAmount | naira }}</span>. Prices, discount and VAT come from the quotation.</p>
        <form id="conv" [formGroup]="form" (ngSubmit)="convert()" class="form-grid" novalidate>
          <div class="field"><label for="cw">Take stock from</label>
            <select id="cw" class="input" formControlName="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
          <div class="field"><label for="cm">Paid by</label>
            <select id="cm" class="input" formControlName="paymentMethod"><option>Cash</option><option>Bank Transfer</option><option>Card</option><option>Credit</option></select></div>
          <div class="field"><label for="cpn">Amount received now</label><input id="cpn" class="input num" type="number" min="0" step="0.01" formControlName="paidNow" /></div>
          <div class="field"><label for="cd">Payment due by</label><input id="cd" class="input" type="date" formControlName="dueDate" /></div>
        </form>
        @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="converting.set(null)">Cancel</button>
        <button type="submit" form="conv" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ busy() ? 'Saving…' : 'Save sale' }}</button>
      </ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; } .lede { margin: 0 0 1rem; max-width: 46rem; } .actions { white-space: nowrap; }`,
})
export class QuotationsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly auth = inject(Auth);
  protected readonly list = new PagedList<QuotationRow>(q => this.api2.quotations(q));
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly converting = signal<QuotationRow | null>(null);
  protected readonly emailing = signal<QuotationRow | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly form = this.fb.nonNullable.group({ warehouseId: [0, Validators.min(1)], paymentMethod: ['Cash'], paidNow: [0, Validators.min(0)], dueDate: [''] });

  async ngOnInit() {
    void this.list.load();
    try { const ws = await this.api.warehouses(); this.warehouses.set(ws); if (ws[0]) this.form.controls.warehouseId.setValue(ws[0].id); } catch { /* the convert dialog will show nothing to pick */ }
  }

  protected openConvert(q: QuotationRow) { this.error.set(''); this.form.patchValue({ paidNow: 0, dueDate: '' }); this.converting.set(q); }

  protected async convert() {
    const q = this.converting(); if (!q || this.form.invalid) return;
    const v = this.form.getRawValue();
    this.busy.set(true); this.error.set('');
    try {
      const r = await this.api2.convertQuotation(q.id, { warehouseId: Number(v.warehouseId), paymentMethod: v.paymentMethod, paidNow: Number(v.paidNow) || 0, dueDate: v.dueDate || null });
      this.toasts.ok(`Sale ${r.invoiceNumber} saved from ${q.quotationNumber}.`);
      this.converting.set(null); await this.list.load();
    } catch (e) {
      const p = problemOf(e);
      this.error.set(p?.shortfalls?.length ? p.shortfalls.map(s => `${s.productName}: asked for ${s.requested}, only ${s.available} available.`).join(' ') : messageOf(e));
    } finally { this.busy.set(false); }
  }

  protected async remove(q: QuotationRow) {
    const ok = await this.confirm.ask({ title: `Delete ${q.quotationNumber}?`, message: 'Only quotations that have not become a sale can be deleted.', confirmLabel: 'Delete quotation', danger: true });
    if (ok === null) return;
    try { await this.api2.deleteQuotation(q.id); this.toasts.ok('Quotation deleted.'); await this.list.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
