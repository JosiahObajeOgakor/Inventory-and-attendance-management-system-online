import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { InvoiceDetail } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe, NairaPipe, StampTimePipe, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-sale-detail',
  imports: [RouterLink, Icon, NairaPipe, DayPipe, StampTimePipe, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head no-print">
        <h1>Sale</h1>
        <div class="actions">
          <a class="btn" routerLink="/sales">All sales</a>
          <a class="btn btn-primary" [href]="'/api/sales/' + id() + '/receipt.pdf'" target="_blank" rel="noopener"><app-icon name="print" [size]="18" /> Receipt (PDF)</a>
          <button type="button" class="btn" (click)="print()">Print this page</button>
          @if (auth.isAdmin() && s() && s()!.status !== 'Voided') { <button type="button" class="btn btn-danger" (click)="voidIt()">Void sale</button> }
        </div>
      </div>

      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      @if (s(); as s) {
        <article class="card receipt">
          @if (s.status === 'Voided') { <div class="void-band">Voided — {{ s.voidReason }}</div> }
          <header>
            <div><span class="eyebrow">{{ auth.companyName() }}</span><h2 class="docno">{{ s.invoiceNumber }}</h2></div>
            <div class="right"><app-stamp [label]="s.status" /><div class="muted">{{ s.invoiceDate | day }}</div></div>
          </header>

          <dl class="meta">
            <div><dt>Customer</dt><dd>{{ s.customer }} <span class="muted">({{ s.customerType }})</span></dd></div>
            <div><dt>Served by</dt><dd>{{ s.createdBy }}</dd></div>
            <div><dt>Price list</dt><dd>{{ s.priceTier }}</dd></div>
            <div><dt>Paid by</dt><dd>{{ s.paymentMethod }}</dd></div>
            @if (s.dueDate) { <div><dt>Due</dt><dd>{{ s.dueDate | day }}</dd></div> }
          </dl>

          <div class="table-wrap"><table class="table">
            <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Line total</th>@if (auth.isAdmin()) { <th class="num no-print">Unit cost</th> }</tr></thead>
            <tbody>
              @for (i of s.items; track i.productId) {
                <tr><td><span class="strong">{{ i.product }}</span><div class="muted mono sm">{{ i.sku }}</div></td>
                  <td class="num mono">{{ i.quantity }} {{ i.unit }}</td><td class="num mono">{{ i.unitPrice | naira }}</td><td class="num mono">{{ i.lineTotal | naira }}</td>
                  @if (auth.isAdmin()) { <td class="num mono muted no-print">{{ i.unitCost | naira }}</td> }</tr>
              }
            </tbody>
          </table></div>

          <div class="foot">
            <div class="pay">
              @if (s.payments.length) {
                <span class="eyebrow">Payments</span>
                @for (p of s.payments; track $index) { <div class="row"><span>{{ p.at | stampTime }} · {{ p.method }}</span><span class="mono">{{ p.amount | naira }}</span></div> }
              }
            </div>
            <dl class="totals">
              <div><dt>Subtotal</dt><dd class="mono">{{ s.subtotal | naira }}</dd></div>
              @if (s.discountAmount > 0) { <div><dt>Discount ({{ s.discountPct }}%)</dt><dd class="mono">−{{ s.discountAmount | naira }}</dd></div> }
              @if (s.vatAmount > 0) { <div><dt>VAT ({{ s.vatRate }}%)</dt><dd class="mono">{{ s.vatAmount | naira }}</dd></div> }
              <div class="grand"><dt>Total</dt><dd class="figure">{{ s.totalAmount | naira }}</dd></div>
              <div><dt>Paid</dt><dd class="mono">{{ s.amountPaid | naira }}</dd></div>
              @if (owed() > 0 && s.status !== 'Voided') { <div class="owed"><dt>Balance on this invoice</dt><dd class="mono">{{ owed() | naira }}</dd></div> }
            </dl>
          </div>
        </article>
      } @else if (!error()) { <div class="card skeleton" style="height:18rem"></div> }
    </div>`,
  styles: `
    .receipt { max-width: 52rem; padding: 1.5rem 1.75rem 1.75rem; position: relative; overflow: hidden; }
    header { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; padding-bottom: 1rem; border-bottom: 2px solid var(--ink); }
     .right { text-align: right; display: flex; flex-direction: column; gap: .35rem; align-items: flex-end; }
    .void-band { position: absolute; inset: 0 0 auto 0; background: var(--stamp); color: #fff; padding: .4rem 1.75rem; font-weight: 600; font-size: .875rem; }
    .void-band + header { margin-top: 1.6rem; }
    .meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: .8rem 1.5rem; margin: 1.1rem 0; } .meta dt { font: 500 .6875rem/1 var(--font-mono); text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin-bottom: .25rem; } .meta dd { margin: 0; }
    .sm { font-size: .75rem; }
    .foot { display: flex; justify-content: space-between; gap: 2rem; margin-top: 1.25rem; flex-wrap: wrap; } .pay { flex: 1 1 14rem; display: flex; flex-direction: column; gap: .3rem; font-size: .875rem; } .row { display: flex; justify-content: space-between; gap: 1rem; }
    .totals { margin: 0; width: min(20rem, 100%); display: grid; gap: .35rem; margin-left: auto; } .totals div { display: flex; justify-content: space-between; } .totals dt { color: var(--muted); } .totals dd { margin: 0; }
    .grand { border-top: 2px solid var(--ink); padding-top: .5rem; align-items: baseline; } .grand dt { color: var(--ink) !important; font-weight: 700; } .grand dd { font-size: 1.9rem; color: var(--brand); }
    .owed dt, .owed dd { color: var(--stamp) !important; font-weight: 700; }
    @media print { .receipt { max-width: none; border: 0; padding: 0; } .grand dd { color: #000; } }
  `,
})
export class SaleDetail implements OnInit {
  readonly id = input.required<string>();   // bound from the route (withComponentInputBinding)
  private readonly api = inject(Api);
  private readonly confirm = inject(Confirm);
  private readonly toasts = inject(Toasts);
  protected readonly auth = inject(Auth);
  protected readonly s = signal<InvoiceDetail | null>(null);
  protected readonly error = signal('');
  protected readonly owed = computed(() => (this.s() ? this.s()!.totalAmount - this.s()!.amountPaid : 0));

  ngOnInit() { void this.load(); }

  private async load() {
    try { this.s.set(await this.api.sale(Number(this.id()))); } catch (e) { this.error.set(messageOf(e)); }
  }

  protected print() { window.print(); }

  protected async voidIt() {
    const s = this.s(); if (!s) return;
    const reason = await this.confirm.ask({
      title: 'Void this sale?',
      message: `${s.invoiceNumber} stays on record as voided. The stock goes back on the shelf and the customer’s balance drops by what they still owed. Money already received stays recorded as paid — note any refund below.`,
      confirmLabel: 'Void sale', danger: true, reason: { label: 'Why is it being voided?', required: true },
    });
    if (reason === null) return;
    try { await this.api.voidSale(s.id, reason); this.toasts.ok(`${s.invoiceNumber} voided.`); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
}
