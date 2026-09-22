import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { PurchaseDetail as Detail, Warehouse } from '../../core/models';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe, NairaPipe, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-purchase-detail',
  imports: [RouterLink, FormsModule, Modal, NairaPipe, DayPipe, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Purchase</h1>
        <div class="actions">
          <a class="btn" routerLink="/purchases">All purchases</a>
          @if (p(); as p) {
            @if (p.status === 'Pending') { <button type="button" class="btn btn-primary" (click)="receiveOpen.set(true)">Receive goods</button> }
            @if (p.paymentStatus !== 'Paid') { <button type="button" class="btn" (click)="markPaid()">Mark as paid</button> }
          }
        </div>
      </div>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      @if (p(); as p) {
        <article class="card doc">
          <header><div><span class="eyebrow">{{ p.supplier }}</span><h2 class="docno">{{ p.poNumber }}</h2></div>
            <div class="right"><div class="stamps"><app-stamp [label]="p.status" /><app-stamp [label]="p.paymentStatus" /></div><div class="muted">{{ p.orderDate | day }}</div></div></header>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Unit cost</th><th class="num">Line total</th></tr></thead>
            <tbody>@for (i of p.items; track i.productId) { <tr><td><span class="strong">{{ i.product }}</span><div class="muted mono sm">{{ i.sku }}</div></td><td class="num mono">{{ i.quantity }}</td><td class="num mono">{{ i.unitCost | naira }}</td><td class="num mono">{{ i.lineTotal | naira }}</td></tr> }</tbody>
          </table></div>
          <dl class="totals">
            <div><dt>Total</dt><dd class="figure">{{ p.totalAmount | naira }}</dd></div>
            <div><dt>Paid</dt><dd class="mono">{{ p.amountPaid | naira }}</dd></div>
            @if (owed() > 0) { <div class="owed"><dt>Still owed</dt><dd class="mono">{{ owed() | naira }}</dd></div> }
          </dl>
        </article>
      } @else if (!error()) { <div class="card skeleton" style="height:14rem"></div> }
    </div>

    <app-modal [open]="receiveOpen()" heading="Receive goods" (closed)="receiveOpen.set(false)">
      <p>Add everything on this order to stock. This can only be done once.</p>
      <div class="field" style="margin-top:.9rem"><label for="rw">Put the goods in</label>
        <select id="rw" class="input" [(ngModel)]="warehouseId">@for (w of warehouses(); track w.id) { <option [ngValue]="w.id">{{ w.name }}</option> }</select></div>
      <ng-container modal-actions><button type="button" class="btn" (click)="receiveOpen.set(false)">Cancel</button><button type="button" class="btn btn-primary" [disabled]="busy()" (click)="receive()">Add to stock</button></ng-container>
    </app-modal>`,
  styles: `
    .doc { max-width: 52rem; padding: 1.5rem 1.75rem; } header { display: flex; justify-content: space-between; gap: 1rem; padding-bottom: 1rem; border-bottom: 2px solid var(--ink); margin-bottom: .5rem; }
     .right { display: flex; flex-direction: column; align-items: flex-end; gap: .4rem; } .stamps { display: flex; gap: .4rem; } .sm { font-size: .75rem; }
    .totals { margin: 1.1rem 0 0 auto; width: min(20rem, 100%); display: grid; gap: .35rem; } .totals div { display: flex; justify-content: space-between; align-items: baseline; } .totals dt { color: var(--muted); } .totals dd { margin: 0; }
    .totals .figure { font-size: 1.9rem; color: var(--brand); } .owed dt, .owed dd { color: var(--stamp) !important; font-weight: 700; }
  `,
})
export class PurchaseDetail implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly p = signal<Detail | null>(null);
  protected readonly error = signal('');
  protected readonly warehouses = signal<Warehouse[]>([]);
  protected readonly receiveOpen = signal(false);
  protected readonly busy = signal(false);
  protected warehouseId = 0;
  protected readonly owed = computed(() => (this.p() ? this.p()!.totalAmount - this.p()!.amountPaid : 0));

  async ngOnInit() {
    await this.load();
    try { const w = await this.api.warehouses(); this.warehouses.set(w); this.warehouseId = w[0]?.id ?? 0; } catch { /* the receive dialog will show an empty list */ }
  }

  private async load() { try { this.p.set(await this.api.purchase(Number(this.id()))); } catch (e) { this.error.set(messageOf(e)); } }

  protected async receive() {
    this.busy.set(true);
    try { await this.api.receivePurchase(Number(this.id()), this.warehouseId); this.toasts.ok('Goods added to stock.'); this.receiveOpen.set(false); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async markPaid() {
    const p = this.p(); if (!p) return;
    const ok = await this.confirm.ask({ title: 'Mark as paid?', message: `This records a payment of the remaining amount on ${p.poNumber} and reduces what you owe ${p.supplier}.`, confirmLabel: 'Mark as paid' });
    if (ok === null) return;
    try { await this.api.payPurchase(p.id); this.toasts.ok('Marked as paid.'); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
