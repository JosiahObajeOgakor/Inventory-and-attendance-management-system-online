import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { Icon } from '../../shared/icon';
import { Toasts } from '../../shared/feedback';
import { PagedList } from '../../shared/paged-list';
import { InvoiceRow } from '../../core/models';
import { DayPipe, NairaPipe, Pager, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-sales-list',
  imports: [RouterLink, Icon, NairaPipe, DayPipe, Stamp, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Sales</h1>
        <div class="actions">
          <a class="btn" href="/api/exports/invoices" download><app-icon name="download" [size]="18" /> Excel</a><a class="btn btn-primary" routerLink="/sales/new"><app-icon name="plus" [size]="18" /> New sale</a></div>
      </div>

      <section class="card">
        <div class="toolbar">
          <div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search invoice, customer, phone or status" aria-label="Search sales" (input)="list.setSearch($any($event.target).value)" /></div>
          <div class="search scan"><app-icon name="scan" [size]="17" />
            <input class="input" placeholder="Scan a receipt" aria-label="Scan a receipt" (keydown.enter)="scan($any($event.target))" /></div>
        </div>

        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap">
          <table class="table">
            <thead><tr><th>Invoice</th><th>Customer</th><th>Date</th><th>Paid by</th><th class="num">Total</th><th>Status</th>@if (auth.isAdmin()) { <th class="num">Est. profit</th> }</tr></thead>
            <tbody>
              @for (i of list.items(); track i.id) {
                <tr class="clickable" [routerLink]="['/sales', i.id]">
                  <td class="mono strong">{{ i.invoiceNumber }}</td><td>{{ i.customer }}</td><td>{{ i.invoiceDate | day }}</td><td>{{ i.paymentMethod }}</td>
                  <td class="num mono">{{ i.totalAmount | naira }}</td><td><app-stamp [label]="i.status" /></td>
                  @if (auth.isAdmin()) { <td class="num mono">{{ i.estProfit | naira }}</td> }
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (!list.loading() && !list.items().length) {
          <div class="empty"><strong>{{ list.search() ? 'No sales match that search' : 'No sales yet' }}</strong>{{ list.search() ? 'Try a different invoice number, customer or phone.' : 'Sales you record will appear here.' }}</div>
        }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>`,
  styles: `.scan { width: 14rem; } @media (max-width: 640px) { .scan { width: 100%; } }`,
})
export class SalesList implements OnInit {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  protected readonly auth = inject(Auth);
  protected readonly list = new PagedList<InvoiceRow>(q => this.api.sales(q));
  protected readonly busy = signal(false);

  ngOnInit() { void this.list.load(); }

  /** Receipt scanner: plain invoice number or our QR payload. */
  protected async scan(el: HTMLInputElement) {
    const code = el.value.trim();
    if (!code) return;
    el.value = '';
    try { const s = await this.api.saleByNumber(code); await this.router.navigate(['/sales', s.id]); }
    catch { this.toasts.error(`No sale on file for “${code}”.`); }
  }
}
