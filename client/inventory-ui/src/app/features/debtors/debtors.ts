import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Auth } from '../../core/auth.service';
import { Paged } from '../../core/models';
import { Icon } from '../../shared/icon';
import { PagedList } from '../../shared/paged-list';
import { NairaPipe, Pager } from '../../shared/ui';
import { CustomerMetricsDialog } from '../customers/customer-metrics';

interface Debtor {
  customerId: number; name: string; phone: string | null; email: string | null; customerType: string; owed: number; overdue: number; unpaidInvoices: number;
  oldestUnpaid: string | null; daysOutstanding: number | null; lastPaymentOn: string | null; lastPaymentAmount: number | null; creditLimit: number;
}
interface DebtorsResult extends Paged<Debtor> { totalOwed: number; totalOverdue: number; overdueCustomers: number; }

/** Everyone who owes this business money, with how long and how much is overdue; one tap to see their history, record a payment, or remind them on WhatsApp. */
@Component({
  selector: 'app-debtors',
  imports: [Icon, NairaPipe, Pager, RouterLink, CustomerMetricsDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Debtors</h1></div>
      <p class="muted lede">Everyone who owes {{ company() }} money. Overdue means past the invoice's due date.</p>

      <div class="kpis">
        <div class="card kpi"><span class="eyebrow">Owed to us</span><div class="value money">{{ totals().owed | naira }}</div><span class="muted sm">{{ list.total() }} {{ list.total() === 1 ? 'customer' : 'customers' }}</span></div>
        <div class="card kpi"><span class="eyebrow">Overdue</span><div class="value money neg">{{ totals().overdue | naira }}</div><span class="muted sm">{{ totals().overdueCustomers }} {{ totals().overdueCustomers === 1 ? 'customer' : 'customers' }}</span></div>
      </div>

      <section class="card">
        <div class="toolbar">
          <div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search name, phone, email or place" aria-label="Search debtors" (input)="list.setSearch($any($event.target).value)" /></div>
          <label class="sortby">Sort <select class="input" [value]="sort()" (change)="setSort($any($event.target).value)" aria-label="Sort debtors">
            <option value="owed">Biggest debt</option><option value="overdue">Most overdue</option><option value="oldest">Owing longest</option><option value="name">Name</option></select></label>
        </div>
        @if (list.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ list.error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Customer</th><th class="num">Owes</th><th class="num">Overdue</th><th>Unpaid since</th><th>Last payment</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (d of list.items(); track d.customerId) {
              <tr>
                <td><span class="strong">{{ d.name }}</span><div class="muted sm">{{ d.phone }}{{ d.customerType ? ' · ' + d.customerType : '' }}</div></td>
                <td class="num mono owes">{{ d.owed | naira }}@if (d.creditLimit > 0 && d.owed > d.creditLimit) { <div class="sm neg">over limit</div> }</td>
                <td class="num mono" [class.neg]="d.overdue > 0">{{ d.overdue > 0 ? (d.overdue | naira) : '—' }}</td>
                <td>@if (d.oldestUnpaid) { {{ d.oldestUnpaid }} <div class="muted sm">{{ d.daysOutstanding }} days · {{ d.unpaidInvoices }} invoice{{ d.unpaidInvoices === 1 ? '' : 's' }}</div> } @else { <span class="muted">Opening balance</span> }</td>
                <td>@if (d.lastPaymentOn) { {{ d.lastPaymentOn }} <div class="muted sm">{{ d.lastPaymentAmount | naira }}</div> } @else { <span class="muted">None yet</span> }</td>
                <td class="actions">
                  <a class="btn btn-sm btn-primary" [routerLink]="['/customers']" [queryParams]="{ q: d.phone || d.name, pay: d.customerId }">Record payment</a>
                  <button type="button" class="btn btn-sm" (click)="historyFor.set(d.customerId)">History</button>
                  @if (whatsapp(d); as link) { <a class="btn btn-sm" [href]="link" target="_blank" rel="noopener">Remind on WhatsApp</a> }
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        @if (!list.loading() && !list.items().length) { <div class="empty"><strong>{{ list.search() ? 'No debtor matches' : 'Nobody owes you money' }}</strong>{{ list.search() ? 'Try another name or number.' : 'Every customer is paid up.' }}</div> }
        <app-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" [sizeable]="true" (sizeChange)="list.setSize($event)" />
      </section>
    </div>
    <app-customer-metrics [customerId]="historyFor()" (closed)="historyFor.set(null)" />`,
  styles: `
    .lede { margin: 0 0 1rem; } .sm { font-size: .8rem; } .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 1rem; margin-bottom: 1rem; }
    .neg { color: var(--danger, #b42318); } .owes { font-weight: 600; } .sortby { display: inline-flex; gap: .5rem; align-items: center; white-space: nowrap; }
  `,
})
export class DebtorsPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(Auth);
  protected readonly sort = signal('owed');
  protected readonly historyFor = signal<number | null>(null);
  protected readonly totals = signal({ owed: 0, overdue: 0, overdueCustomers: 0 });
  protected readonly company = computed(() => this.auth.me()?.companies.find(c => c.key === this.auth.company())?.displayName ?? 'the business');
  protected readonly list = new PagedList<Debtor>(async q => {
    const r = await firstValueFrom(this.http.get<DebtorsResult>('/api/debtors', { params: { page: q.page ?? 1, pageSize: q.pageSize ?? 25, search: q.search ?? '', sort: this.sort() } }));
    this.totals.set({ owed: r.totalOwed, overdue: r.totalOverdue, overdueCustomers: r.overdueCustomers });
    return r;
  }, 25);

  ngOnInit() { void this.list.load(); }
  protected setSort(s: string) { this.sort.set(s); this.list.goTo(1); }

  /** A ready-written, polite reminder in the admin's own WhatsApp (nothing is sent by the server). */
  protected whatsapp(d: Debtor): string | null {
    const digits = (d.phone ?? '').replace(/\D/g, '');
    const intl = digits.startsWith('0') && digits.length === 11 ? '234' + digits.slice(1) : digits;
    if (intl.length < 10) return null;
    const first = d.name.trim().split(/\s+/)[0];
    const amount = '₦' + d.owed.toLocaleString('en-NG', { maximumFractionDigits: 2 });
    const text = `Hello ${first}, a friendly reminder from ${this.company()}: ${amount} is outstanding on your account. Thank you for your business.`;
    return `https://wa.me/${intl}?text=${encodeURIComponent(text)}`;
  }
}
