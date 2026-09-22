import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { PendingInvoice, WaybillRow } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { DayPipe, NairaPipe, Pager } from '../../shared/ui';

@Component({
  selector: 'app-waybills',
  imports: [ReactiveFormsModule, Icon, Modal, DayPipe, NairaPipe, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Waybills</h1>
        <div class="actions"><button type="button" class="btn btn-primary" (click)="openPick()"><app-icon name="plus" [size]="18" /> New waybill</button></div>
      </div>
      <p class="muted lede">A waybill goes with the driver: what is on the truck, where it is going, and a signature line for whoever receives it.</p>
      <section class="card">
        <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
          <input class="input" type="search" placeholder="Search waybill, sale, customer or driver" aria-label="Search waybills" (input)="list.setSearch($any($event.target).value)" /></div></div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Waybill</th><th>Date</th><th>Sale</th><th>Customer</th><th>Driver</th><th>Vehicle</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (w of list.items(); track w.id) {
              <tr><td><span class="docno">{{ w.waybillNumber }}</span></td><td>{{ w.issueDate | day }}</td><td class="mono">{{ w.invoiceNumber }}</td><td class="strong">{{ w.customer }}</td>
                <td>{{ w.driverName ?? '—' }}</td><td class="mono">{{ w.vehiclePlate ?? '—' }}</td>
                <td class="actions"><a class="btn btn-sm" [href]="'/api/waybills/' + w.id + '/pdf'" target="_blank" rel="noopener"><app-icon name="print" [size]="15" /> PDF</a></td></tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No waybills yet</strong>Choose a sale that is going out and fill in the driver and vehicle.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>

    <app-modal [open]="pickOpen()" heading="Which sale is being dispatched?" [wide]="true" (closed)="pickOpen.set(false)">
      <div class="toolbar" style="padding-left:0;padding-right:0"><div class="search grow"><app-icon name="search" [size]="17" />
        <input class="input" type="search" placeholder="Search sale number or customer" aria-label="Search sales" (input)="pending.setSearch($any($event.target).value)" /></div>
        <label class="check"><input type="checkbox" [checked]="pendingOnly()" (change)="togglePending($any($event.target).checked)" /> Only sales without a waybill</label></div>
      <div class="table-wrap"><table class="table">
        <thead><tr><th>Sale</th><th>Date</th><th>Customer</th><th class="num">Total</th><th></th></tr></thead>
        <tbody>
          @for (i of pending.items(); track i.invoiceId) {
            <tr><td class="mono strong">{{ i.invoiceNumber }}</td><td>{{ i.invoiceDate | day }}</td><td>{{ i.customer }}@if (i.waybillCount) { <span class="muted sm"> · {{ i.waybillCount }} waybill(s)</span> }</td>
              <td class="num mono">{{ i.totalAmount | naira }}</td><td class="actions"><button type="button" class="btn btn-sm btn-primary" (click)="choose(i)">Choose</button></td></tr>
          }
        </tbody>
      </table></div>
      @if (!pending.loading() && !pending.items().length) { <div class="empty"><strong>Nothing to dispatch</strong>Every recent sale already has a waybill.</div> }
      <app-pager [page]="pending.page()" [pageSize]="pending.pageSize" [total]="pending.total()" (pageChange)="pending.goTo($event)" />
    </app-modal>

    <app-modal [open]="!!chosen()" heading="Dispatch details" (closed)="chosen.set(null)">
      @if (chosen(); as c) {
        <p>{{ c.invoiceNumber }} for <strong>{{ c.customer }}</strong>@if (fromWarehouse()) { — leaving {{ fromWarehouse() }} }.</p>
        <form id="wb" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
          <div class="field"><label for="wd">Driver’s name</label><input id="wd" class="input" formControlName="driverName" /></div>
          <div class="field"><label for="wp">Driver’s phone</label><input id="wp" class="input" inputmode="tel" formControlName="driverPhone" /></div>
          <div class="field"><label for="wv">Vehicle plate</label><input id="wv" class="input" formControlName="vehiclePlate" /></div>
          <div class="field"><label for="wi">Issue date</label><input id="wi" class="input" type="date" formControlName="issueDate" /></div>
          <div class="field span-2"><label for="wa">Delivery address</label><input id="wa" class="input" formControlName="destinationAddress" /></div>
          <div class="field span-2"><label for="wn">Notes</label><input id="wn" class="input" formControlName="notes" /></div>
        </form>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="chosen.set(null)">Back</button>
        <button type="submit" form="wb" class="btn btn-primary" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Create waybill' }}</button>
      </ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; } .lede { margin: 0 0 1rem; max-width: 46rem; } .actions { white-space: nowrap; }`,
})
export class WaybillsPage implements OnInit {
  private readonly api = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  protected readonly list = new PagedList<WaybillRow>(q => this.api.waybills(q));
  protected readonly pendingOnly = signal(true);
  protected readonly pending = new PagedList<PendingInvoice>(q => this.api.waybillInvoices({ ...q, pendingOnly: this.pendingOnly() }), 8);
  protected readonly pickOpen = signal(false);
  protected readonly chosen = signal<PendingInvoice | null>(null);
  protected readonly fromWarehouse = signal('');
  protected readonly busy = signal(false);
  protected readonly form = this.fb.nonNullable.group({ driverName: [''], driverPhone: [''], vehiclePlate: [''], issueDate: [''], destinationAddress: [''], notes: [''] });

  ngOnInit() { void this.list.load(); }
  protected openPick() { this.pickOpen.set(true); this.pending.page.set(1); void this.pending.load(); }
  protected togglePending(on: boolean) { this.pendingOnly.set(on); this.pending.page.set(1); void this.pending.load(); }

  protected async choose(i: PendingInvoice) {
    try {
      const p = await this.api.waybillPrefill(i.invoiceId);
      this.form.reset({ driverName: '', driverPhone: p.phone, vehiclePlate: '', issueDate: '', destinationAddress: p.address, notes: '' });
      this.fromWarehouse.set(p.warehouse);
      this.pickOpen.set(false); this.chosen.set(i);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async save() {
    const c = this.chosen(); if (!c) return;
    const v = this.form.getRawValue(); const n = (s: string) => s.trim() || null;
    this.busy.set(true);
    try {
      const { id } = await this.api.createWaybill({ invoiceId: c.invoiceId, issueDate: v.issueDate || null, driverName: n(v.driverName), driverPhone: n(v.driverPhone),
        vehiclePlate: n(v.vehiclePlate), destinationAddress: n(v.destinationAddress), notes: n(v.notes) });
      this.toasts.ok('Waybill created.'); this.chosen.set(null); await this.list.load();
      window.open(`/api/waybills/${id}/pdf`, '_blank', 'noopener');
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }
}
