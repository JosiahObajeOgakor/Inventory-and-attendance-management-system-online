import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { Dashboard } from '../../core/models';
import { DayPipe, NairaPipe, Stamp } from '../../shared/ui';
import { AdminDashboard } from './admin-dashboard';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, NairaPipe, DayPipe, Stamp, AdminDashboard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (auth.isAdmin()) { <app-admin-dashboard /> } @else {
    <div class="page">
      <div class="page-head">
        <h1>Dashboard</h1>
        <div class="actions"><a class="btn btn-primary" routerLink="/sales/new">New sale</a></div>
      </div>

      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (d(); as d) {
        <div class="grid" [class.cols-4]="auth.isAdmin()" [class.cols-3]="!auth.isAdmin()">
          <div class="card kpi"><span class="eyebrow">Stock value at cost</span><div class="value money">{{ d.stockValue | naira }}</div></div>
          <div class="card kpi" [class.warn]="d.lowStockCount > 0">
            <span class="eyebrow">Batches at or below reorder level</span>
            <div class="value">{{ d.lowStockCount }}</div>
          </div>
          <div class="card kpi"><span class="eyebrow">Invoices today</span><div class="value">{{ d.todaysInvoices }}</div></div>
          @if (auth.isAdmin()) {
            <div class="card kpi">
              <span class="eyebrow">Gross profit this month</span>
              <div class="value money">{{ d.grossProfitThisMonth | naira }}</div>
              <div class="sub">Expenses so far: {{ d.expensesThisMonth | naira }}</div>
            </div>
          }
        </div>

        <div class="grid lower">
          @if (d.reorder) {
            <section class="card span">
              <h2>Needs reordering</h2>
              @if (d.reorder.length) {
                <div class="table-wrap"><table class="table">
                  <thead><tr><th>Product</th><th class="num">On hand</th><th class="num">Cover</th><th class="num">Suggested order</th><th>Status</th></tr></thead>
                  <tbody>
                    @for (r of d.reorder; track r.productId) {
                      <tr><td class="strong">{{ r.product }}</td><td class="num mono">{{ r.onHand }}</td>
                        <td class="num mono">{{ r.daysOfCover === null ? '—' : r.daysOfCover + ' d' }}</td>
                        <td class="num mono">{{ r.suggestedOrderQty }}</td><td><app-stamp [label]="r.urgency" /></td></tr>
                    }
                  </tbody>
                </table></div>
              } @else { <div class="empty"><strong>Nothing to reorder</strong>Every product has enough cover for now.</div> }
            </section>
          }

          <section class="card">
            <h2>Low or expiring stock</h2>
            @if (d.lowStock.length) {
              <div class="table-wrap"><table class="table">
                <thead><tr><th>Product</th><th>Warehouse</th><th class="num">Qty</th><th>Status</th></tr></thead>
                <tbody>
                  @for (b of d.lowStock; track b.batchId) {
                    <tr><td class="strong">{{ b.product }}<div class="muted mono small">{{ b.batchNumber }}</div></td><td>{{ b.warehouse }}</td>
                      <td class="num mono">{{ b.quantity }}</td><td><app-stamp [label]="b.status" /></td></tr>
                  }
                </tbody>
              </table></div>
            } @else { <div class="empty"><strong>Stock looks healthy</strong>No batch is at or below its reorder level.</div> }
          </section>

          <section class="card">
            <h2>Recent sales</h2>
            @if (d.recentInvoices.length) {
              <div class="table-wrap"><table class="table">
                <thead><tr><th>Invoice</th><th>Customer</th><th class="num">Total</th><th>Status</th></tr></thead>
                <tbody>
                  @for (i of d.recentInvoices; track i.id) {
                    <tr class="clickable" [routerLink]="['/sales', i.id]">
                      <td class="mono">{{ i.invoiceNumber }}<div class="muted small">{{ i.invoiceDate | day }}</div></td>
                      <td>{{ i.customer }}</td><td class="num mono">{{ i.totalAmount | naira }}</td><td><app-stamp [label]="i.status" /></td></tr>
                  }
                </tbody>
              </table></div>
            } @else { <div class="empty"><strong>No sales yet</strong>Record the first one from “New sale”.</div> }
          </section>
        </div>
      } @else if (!error()) {
        <div class="grid cols-3"><div class="card skeleton" style="height:6rem"></div><div class="card skeleton" style="height:6rem"></div><div class="card skeleton" style="height:6rem"></div></div>
      }
    </div>
    }`,
  styles: `.lower { margin-top: 1rem; align-items: start; grid-template-columns: repeat(auto-fit, minmax(min(30rem, 100%), 1fr)); } .span { grid-column: 1 / -1; } .small { font-size: .75rem; } .table td .small { margin-top: .1rem; }`,
})
export class DashboardPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly auth = inject(Auth);
  protected readonly d = signal<Dashboard | null>(null);
  protected readonly error = signal('');

  async ngOnInit() {
    if (this.auth.isAdmin()) return;   // administrators get the live overview instead
    try { this.d.set(await this.api.dashboard()); } catch (e) { this.error.set(messageOf(e)); }
  }
}
