import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { PurchaseRow } from '../../core/models';
import { Icon } from '../../shared/icon';
import { PagedList } from '../../shared/paged-list';
import { DayPipe, NairaPipe, Pager, Stamp } from '../../shared/ui';

@Component({
  selector: 'app-purchases-list',
  imports: [RouterLink, Icon, NairaPipe, DayPipe, Stamp, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Purchases</h1>
        <div class="actions"><a class="btn btn-primary" routerLink="/purchases/new"><app-icon name="plus" [size]="18" /> New purchase</a></div>
      </div>
      <section class="card">
        <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
          <input class="input" type="search" placeholder="Search order number, supplier or status" aria-label="Search purchases" (input)="list.setSearch($any($event.target).value)" /></div></div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Order</th><th>Supplier</th><th>Date</th><th>Goods</th><th>Payment</th><th class="num">Total</th><th class="num">Still owed</th></tr></thead>
          <tbody>
            @for (p of list.items(); track p.id) {
              <tr class="clickable" [routerLink]="['/purchases', p.id]"><td class="mono strong">{{ p.poNumber }}</td><td>{{ p.supplier }}</td><td>{{ p.orderDate | day }}</td>
                <td><app-stamp [label]="p.status" /></td><td><app-stamp [label]="p.paymentStatus" /></td>
                <td class="num mono">{{ p.totalAmount | naira }}</td><td class="num mono">{{ p.totalAmount - p.amountPaid | naira }}</td></tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>No purchases yet</strong>Record what you buy from suppliers here.</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
      </section>
    </div>`,
})
export class PurchasesList implements OnInit {
  private readonly api = inject(Api);
  protected readonly list = new PagedList<PurchaseRow>(q => this.api.purchases(q));
  ngOnInit() { void this.list.load(); }
}
