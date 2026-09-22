import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { CalendarData, CustomerHero, InsightSet, Kpi, Overview } from '../../core/models-dash';
import { Icon } from '../../shared/icon';
import { DueCalendar } from './due-calendar';
import { Bubbles, Dots, LineChart, Series, compact, naira } from './widgets';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const POLL_MS = 30_000;

@Component({
  selector: 'app-admin-dashboard',
  imports: [RouterLink, Icon, LineChart, Dots, Bubbles, DueCalendar],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="dash">
      <header class="top">
        <div>
          <h1>Dashboard</h1>
          <p class="crumb">{{ auth.companyName() }} <span>›</span> Overview</p>
        </div>
        <div class="right">
          <span class="live" [class.stale]="stale()" role="status"><i></i>{{ stale() ? 'Reconnecting…' : 'Live · updated ' + updated() }}</span>
          <a class="add" routerLink="/sales/new"><app-icon name="plus" [size]="18" /> New sale</a>
        </div>
      </header>

      @if (error() && !o()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (o(); as o) {
        <!-- KPIs -->
        <section class="kpis" aria-label="This month">
          @for (k of kpis(); track k.label) {
            <article class="card">
              <div class="k-head"><span>{{ k.label }}</span><app-icon [name]="k.icon" [size]="16" /></div>
              <div class="k-val">{{ full(k.kpi.current) }}</div>
              <div class="k-foot">
                <span class="prev">vs last month {{ full(k.kpi.previous) }}</span>
                @if (k.kpi.changePct !== null) {
                  <span class="chip" [class.good]="good(k)" [class.bad]="!good(k)"><span aria-hidden="true">{{ k.kpi.changePct >= 0 ? '↑' : '↓' }}</span> {{ k.kpi.changePct >= 0 ? '+' : '' }}{{ k.kpi.changePct }}%</span>
                } @else { <span class="chip">new</span> }
              </div>
            </article>
          }
        </section>

        <!-- customers by month -->
        <section class="card hero">
          <div class="c-head"><app-icon name="customer" [size]="18" /><h2>Top customers</h2>
            <span class="sub">{{ o.customers }} {{ o.customers === 1 ? 'customer' : 'customers' }} · receivables <b>{{ full(o.receivablesTotal) }}</b>, overdue <b class="neg">{{ full(o.overdueTotal) }}</b></span></div>
          @if (o.topCustomers.length) {
            <div class="people">
              @for (c of o.topCustomers; track c.id) {
                @let m = monthOf(c);
                <div class="person">
                  <div class="cols">
                    <div class="colw"><span class="vt">Sales</span><app-dots [fraction]="frac(m.sales, maxSales())" label="Sales" [text]="full(m.sales)" /></div>
                    <div class="colw"><span class="vt">Gross profit</span><app-dots [fraction]="frac(m.grossProfit, maxProfit())" label="Gross profit" [text]="full(m.grossProfit)" /></div>
                    <div class="colw"><span class="vt">Profitability</span><app-dots [fraction]="m.profitability / 100" label="Profitability" [text]="m.profitability + '%'" color="#22d3b8" /></div>
                    <div class="colw"><span class="vt">Open balance</span><app-dots [fraction]="frac(c.openBalance, maxBalance())" label="Open balance" [text]="full(c.openBalance)" color="#ef5b52" /></div>
                    <div class="mark" aria-hidden="true"><b>{{ initials(c.name) }}</b></div>
                  </div>
                  <div class="who"><span class="badge" [class]="c.ranking.toLowerCase()">{{ c.ranking[0] }}</span><strong>{{ c.name }}</strong></div>
                  <div class="nums"><span>{{ compact(m.sales) }} sales</span><span>{{ m.profitability }}% margin</span></div>
                </div>
              }
            </div>
            <div class="timeline">
              <button type="button" class="play" (click)="togglePlay()" [attr.aria-label]="playing() ? 'Pause' : 'Play through the months'">
                @if (playing()) { <svg width="16" height="16" viewBox="0 0 16 16"><rect x="3" y="2" width="3.5" height="12" rx="1" fill="#fff"/><rect x="9.5" y="2" width="3.5" height="12" rx="1" fill="#fff"/></svg> }
                @else { <svg width="16" height="16" viewBox="0 0 16 16"><path d="M4 2.5v11l9-5.5z" fill="#fff"/></svg> }
              </button>
              <div class="track">
                <input type="range" min="0" [max]="o.months.length - 1" [value]="mi()" (input)="setMonth(+$any($event.target).value)" aria-label="Month shown" />
                <div class="ticks">
                  @for (m of o.months; track $index; let i = $index) {
                    <button type="button" [class.on]="i === mi()" (click)="setMonth(i)"><span>{{ label(m.month, m.year) }}</span></button>
                  }
                </div>
              </div>
            </div>
          } @else { <div class="empty"><strong>No customer sales yet</strong>Named customers appear here once they buy.</div> }
        </section>

        <!-- revenue chart + advice -->
        <div class="row two-one">
          <section class="card">
            <div class="c-head"><app-icon name="finance" [size]="18" /><h2>Revenue, income and profit</h2>
              <div class="seg" role="group" aria-label="Range"><button type="button" [class.on]="range() === 'days'" (click)="range.set('days')">30 days</button><button type="button" [class.on]="range() === 'months'" (click)="range.set('months')">12 months</button></div></div>
            <app-line-chart [series]="series()" [labels]="chartLabels()" />
            <div class="legend">@for (s of series(); track s.key) { <span><i [style.background]="s.color"></i>{{ s.label }}</span> }
              <span class="hint" title="Revenue is sales before VAT. Income is cash actually received.">?</span></div>
          </section>

          <section class="card advice">
            <div class="c-head"><app-icon name="alert" [size]="18" /><h2>Recommendations</h2>
              <button type="button" class="ghost" (click)="reloadInsights(true)" [disabled]="insightsBusy()" aria-label="Refresh recommendations">{{ insightsBusy() ? '…' : 'Refresh' }}</button></div>
            @if (insights(); as ins) {
              <ul>@for (a of ins.items; track a.title) {
                <li [class]="a.priority"><i aria-hidden="true"></i><div><strong>{{ a.title }}</strong><p>{{ a.body }}</p></div></li>
              }</ul>
              <p class="src">{{ ins.fromAi ? 'Written by the assistant from your records' : 'From rules over your records' }} · {{ ago(ins.generatedAtUtc) }}</p>
            } @else { <div class="skeleton" style="height:12rem"></div> }
          </section>
        </div>

        <!-- calendar + warehouses -->
        <div class="row two-one">
          <section class="card">
            <div class="c-head"><app-icon name="clock" [size]="18" /><h2>Who owes, and when</h2></div>
            @if (cal(); as c) { <app-due-calendar [data]="c" [today]="o.asOf" (step)="stepMonth($event)" /> } @else { <div class="skeleton" style="height:22rem"></div> }
          </section>

          <section class="card">
            <div class="c-head"><app-icon name="stock" [size]="18" /><h2>Stock by warehouse</h2></div>
            <app-bubbles [items]="bubbleItems()" />
            <div class="wh">
              @for (w of o.warehouses; track w.id) {
                <div><strong>{{ w.name }}</strong><span class="mono">{{ w.units.toLocaleString() }} units · {{ compact(w.costValue) }}</span>
                  @if (w.lowBatches || w.expiredUnits || w.expiringSoonUnits) {
                    <small>@if (w.lowBatches) { {{ w.lowBatches }} low } @if (w.expiredUnits) { · {{ w.expiredUnits }} expired } @if (w.expiringSoonUnits) { · {{ w.expiringSoonUnits }} expiring soon }</small>
                  }</div>
              }
              <div class="total"><strong>Total inventory</strong><span class="mono">{{ full(o.inventoryValue) }}</span></div>
            </div>
          </section>
        </div>

        <!-- owing + products -->
        <div class="row two-one">
          <section class="card">
            <div class="c-head"><app-icon name="users" [size]="18" /><h2>Largest overdue balances</h2></div>
            <div class="table-wrap"><table class="t">
              <thead><tr><th>Customer</th><th>Invoice</th><th>Phone</th><th>Due</th><th class="num">Owes</th></tr></thead>
              <tbody>
                @for (d of cal()?.overdue?.slice(0, 5) ?? []; track d.invoiceId) {
                  <tr><td><a [routerLink]="['/sales', d.invoiceId]">{{ d.customer }}</a></td><td class="mono">{{ d.invoiceNumber }}</td><td class="mono">{{ d.phone ?? '—' }}</td>
                    <td>{{ d.daysOverdue }} days late</td><td class="num mono">{{ full(d.outstanding) }}</td></tr>
                } @empty { <tr><td colspan="5" class="none">Nothing overdue. Well done.</td></tr> }
              </tbody>
            </table></div>
          </section>

          <section class="card">
            <div class="c-head"><app-icon name="product" [size]="18" /><h2>Not selling</h2><span class="sub">no sale in 30 days</span></div>
            <ul class="movers">
              @for (p of o.slowMovers; track p.productId) { <li><span>{{ p.name }}</span><span class="mono">{{ p.inStock }} in stock · {{ compact(p.stockValue) }}</span></li> }
              @empty { <li class="none">Everything in stock has sold recently.</li> }
            </ul>
          </section>
        </div>
      } @else if (!error()) {
        <div class="kpis"><div class="card skeleton" style="height:8rem"></div><div class="card skeleton" style="height:8rem"></div><div class="card skeleton" style="height:8rem"></div><div class="card skeleton" style="height:8rem"></div></div>
      }
    </div>`,
  styles: `
    :host { display: block; margin: -1.5rem; padding: 1.5rem; background: #eef2f8; min-height: 100%; }
    .dash { max-width: 90rem; margin: 0 auto; display: flex; flex-direction: column; gap: 1rem; --blue: #3db4ff; --dark: #1c2130; }
    .top { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; flex-wrap: wrap; } h1 { font: 700 1.6rem/1.1 var(--font-body); color: var(--dark); margin: 0; text-transform: none; letter-spacing: 0; }
    .crumb { margin: .3rem 0 0; color: #7a8497; font-size: .8125rem; } .crumb span { margin: 0 .25rem; } .right { display: flex; gap: .7rem; align-items: center; }
    .live { display: inline-flex; align-items: center; gap: .45rem; font-size: .75rem; color: #5b6579; background: #fff; padding: .5rem .8rem; border-radius: 10px; } .live i { width: 8px; height: 8px; border-radius: 50%; background: #1fbf6b; box-shadow: 0 0 0 0 #1fbf6b88; animation: pulse 2s infinite; }
    .live.stale i { background: #f5a524; animation: none; } @keyframes pulse { 70% { box-shadow: 0 0 0 8px #1fbf6b00; } 100% { box-shadow: 0 0 0 0 #1fbf6b00; } }
    .add { display: inline-flex; align-items: center; gap: .4rem; background: var(--blue); color: #fff; padding: .6rem 1rem; border-radius: 10px; font-weight: 600; text-decoration: none; } .add:hover { filter: brightness(.95); }
    .card { background: #fff; border-radius: 16px; padding: 1.1rem 1.25rem; box-shadow: none; border: 0; min-width: 0; }
    .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: 1rem; }
    .k-head { display: flex; justify-content: space-between; align-items: center; color: #4a5468; font-size: .9375rem; padding-bottom: .7rem; border-bottom: 1px solid #eef1f6; } .k-head app-icon { color: #9aa6bd; }
    .k-val { font: 500 1.7rem/1 var(--font-mono); letter-spacing: -.04em; color: var(--dark); margin: .9rem 0 .7rem; white-space: nowrap; }
    .k-foot { display: flex; justify-content: space-between; align-items: center; gap: .5rem; } .prev { font-size: .75rem; color: #8b95a7; }
    .chip { font-size: .75rem; font-weight: 600; padding: .25rem .6rem; border-radius: 99px; background: #f1f4f9; color: #5b6579; } .chip.good { background: #e6f7ee; color: #0a8f4d; } .chip.bad { background: #fdeceb; color: #b3261e; }
    .c-head { display: flex; align-items: center; gap: .55rem; padding-bottom: .8rem; margin-bottom: 1rem; border-bottom: 1px solid #eef1f6; color: #4a5468; flex-wrap: wrap; } .c-head h2 { margin: 0; font: 500 1rem var(--font-body); color: var(--dark); text-transform: none; letter-spacing: 0; }
    .sub { margin-left: auto; font-size: .78rem; color: #8b95a7; } .sub b { color: #4a5468; font-family: var(--font-mono); font-weight: 500; } .sub .neg { color: #b3261e; }
    .hero { padding-bottom: 1.2rem; } .people { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 22rem)); gap: 1.5rem; }
    .cols { display: grid; grid-template-columns: repeat(4, auto) 1fr; align-items: end; gap: .9rem; min-height: 15rem; } .colw { display: flex; flex-direction: column; align-items: center; gap: .6rem; justify-content: flex-end; }
    .vt { writing-mode: vertical-rl; transform: rotate(180deg); font-size: .75rem; color: #8b95a7; height: 5.4rem; white-space: nowrap; }
    .mark { align-self: center; justify-self: end; width: 4.4rem; height: 4.4rem; border-radius: 42% 58% 55% 45%; background: #eef1f6; display: grid; place-items: center; color: #b5bdcc; font: 700 1.4rem var(--font-display); }
    .who { display: flex; align-items: center; gap: .55rem; margin-top: 1rem; } .who strong { font-weight: 500; color: var(--dark); font-size: .9rem; } .nums { display: flex; gap: .8rem; font-size: .72rem; color: #8b95a7; margin: .2rem 0 0 2rem; }
    .badge { width: 20px; height: 20px; border-radius: 50%; display: grid; place-items: center; font-size: .65rem; font-weight: 700; color: #fff; background: #b58b3a; } .badge.gold { background: #e0a91b; } .badge.silver { background: #8b97a8; } .badge.bronze { background: #b0703a; }
    .timeline { display: flex; align-items: center; gap: 1.2rem; margin-top: 1.4rem; } .play { flex: none; width: 40px; height: 40px; border-radius: 50%; border: 0; background: var(--blue); display: grid; place-items: center; cursor: pointer; box-shadow: 0 4px 12px #3db4ff66; }
    .track { position: relative; flex: 1; height: 3rem; } .track input { position: absolute; inset: 0 0 auto 0; width: 100%; margin: 0; accent-color: var(--blue); height: 1.4rem; } .ticks { position: absolute; inset: 1.6rem 0 0 0; display: flex; justify-content: space-between; }
    .ticks button { border: 0; background: none; font-size: .72rem; color: #a0a9b9; cursor: pointer; padding: 0; } .ticks button.on { color: var(--blue); font-weight: 700; }
    .row { display: grid; gap: 1rem; } .two-one { grid-template-columns: minmax(0, 2fr) minmax(0, 1fr); }
    .seg { margin-left: auto; display: inline-flex; background: #f1f4f9; border-radius: 10px; padding: 3px; } .seg button { border: 0; background: none; padding: .3rem .7rem; border-radius: 8px; font-size: .78rem; color: #5b6579; cursor: pointer; } .seg button.on { background: #fff; color: var(--dark); font-weight: 600; box-shadow: 0 1px 3px #0001; }
    .legend { display: flex; gap: 1.2rem; font-size: .78rem; color: #5b6579; margin-top: .5rem; align-items: center; } .legend i { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: .35rem; } .hint { margin-left: auto; width: 18px; height: 18px; border-radius: 50%; background: #eef1f6; display: grid; place-items: center; font-size: .7rem; cursor: help; }
    .advice ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .85rem; } .advice li { display: flex; gap: .7rem; } .advice li i { flex: none; width: 4px; border-radius: 4px; background: #3db4ff; } .advice li.high i { background: #ef5b52; } .advice li.medium i { background: #f5a524; }
    .advice strong { font-weight: 600; color: var(--dark); font-size: .875rem; } .advice p { margin: .15rem 0 0; font-size: .8125rem; line-height: 1.45; color: #5b6579; } .src { margin: 1rem 0 0; font-size: .72rem; color: #9aa6bd; }
    .ghost { margin-left: auto; border: 1px solid #e3e8f0; background: #fff; border-radius: 8px; padding: .25rem .65rem; font-size: .75rem; cursor: pointer; color: #4a5468; }
    .wh { display: flex; flex-direction: column; gap: .5rem; margin-top: 1rem; } .wh > div { display: flex; flex-wrap: wrap; justify-content: space-between; gap: .1rem .6rem; font-size: .84rem; padding-top: .5rem; border-top: 1px solid #eef1f6; } .wh small { flex-basis: 100%; color: #b3261e; font-size: .72rem; } .wh .total { border-top: 2px solid var(--dark); font-size: .9rem; }
    .t { width: 100%; border-collapse: collapse; font-size: .84rem; } .t th { text-align: left; font-weight: 500; color: #7a8497; padding: .55rem .7rem; background: #f6f8fb; } .t th:first-child { border-radius: 8px 0 0 8px; } .t th:last-child { border-radius: 0 8px 8px 0; }
    .t td { padding: .75rem .7rem; border-bottom: 1px solid #f0f3f8; } .t .num { text-align: right; } .t th.num { text-align: right; } .t a { color: #0b6aa8; text-decoration: none; } .t a:hover { text-decoration: underline; } .none { color: #8b95a7; text-align: center; }
    .movers { list-style: none; margin: 0; padding: 0; } .movers li { display: flex; justify-content: space-between; gap: 1rem; padding: .6rem 0; border-bottom: 1px solid #f0f3f8; font-size: .84rem; } .movers .mono { color: #7a8497; font-size: .75rem; }
    @media (max-width: 1100px) { .two-one { grid-template-columns: minmax(0, 1fr); } }
    @media (max-width: 720px) { :host { margin: -1rem; padding: 1rem; } .cols { min-height: 0; } }
  `,
})
export class AdminDashboard implements OnInit {
  private readonly api = inject(Api2);
  protected readonly auth = inject(Auth);
  private readonly destroy = inject(DestroyRef);
  protected readonly compact = compact;
  protected readonly o = signal<Overview | null>(null);
  protected readonly cal = signal<CalendarData | null>(null);
  protected readonly insights = signal<InsightSet | null>(null);
  protected readonly insightsBusy = signal(false);
  protected readonly error = signal('');
  protected readonly stale = signal(false);
  protected readonly updated = signal('');
  protected readonly mi = signal(11);
  protected readonly playing = signal(false);
  protected readonly range = signal<'days' | 'months'>('days');
  private calYear = 0; private calMonth = 0;
  private timers: ReturnType<typeof setInterval>[] = [];

  protected full = naira;
  protected readonly kpis = computed(() => {
    const o = this.o(); if (!o) return [];
    return [
      { label: 'Revenue', icon: 'sale', kpi: o.revenue, invert: false },
      { label: 'Income received', icon: 'cash', kpi: o.collected, invert: false },
      { label: 'Profit', icon: 'finance', kpi: o.netProfit, invert: false },
      { label: 'Losses', icon: 'alert', kpi: o.losses, invert: true },
    ] as { label: string; icon: string; kpi: Kpi; invert: boolean }[];
  });
  protected readonly series = computed<Series[]>(() => {
    const o = this.o(); if (!o) return [];
    if (this.range() === 'days') return [
      { key: 'rev', label: 'Revenue', color: '#3db4ff', values: o.days.map(d => d.revenue) },
      { key: 'inc', label: 'Income received', color: '#22d3b8', values: o.days.map(d => d.collected) },
      { key: 'gp', label: 'Gross profit', color: '#1c2130', values: o.days.map(d => d.grossProfit) },
    ];
    return [
      { key: 'rev', label: 'Revenue', color: '#3db4ff', values: o.months.map(m => m.revenue) },
      { key: 'gp', label: 'Gross profit', color: '#1c2130', values: o.months.map(m => m.grossProfit) },
      { key: 'net', label: 'Profit after expenses', color: '#22d3b8', values: o.months.map(m => m.grossProfit - m.expenses) },
    ];
  });
  protected readonly chartLabels = computed(() => {
    const o = this.o(); if (!o) return [];
    return this.range() === 'days' ? o.days.map(d => `${+d.date.slice(8)} ${MONTHS[+d.date.slice(5, 7) - 1]}`) : o.months.map(m => `${MONTHS[m.month - 1]} ${String(m.year).slice(2)}`);
  });
  protected readonly bubbleItems = computed(() => (this.o()?.warehouses ?? []).filter(w => w.costValue > 0).map(w => ({ name: w.name, value: w.costValue })));
  protected readonly maxSales = computed(() => Math.max(1, ...(this.o()?.topCustomers ?? []).flatMap(c => c.months.map(m => m.sales))));
  protected readonly maxProfit = computed(() => Math.max(1, ...(this.o()?.topCustomers ?? []).flatMap(c => c.months.map(m => m.grossProfit))));
  protected readonly maxBalance = computed(() => Math.max(1, ...(this.o()?.topCustomers ?? []).map(c => c.openBalance)));

  ngOnInit() {
    void this.loadOverview(true);
    void this.reloadInsights(false);
    // "Real time": the figures refresh on their own while this tab is open and visible.
    this.timers.push(setInterval(() => { if (!document.hidden) void this.loadOverview(false); }, POLL_MS));
    document.addEventListener('visibilitychange', this.onVisible);
    this.destroy.onDestroy(() => { this.timers.forEach(clearInterval); document.removeEventListener('visibilitychange', this.onVisible); });
  }
  private readonly onVisible = () => { if (!document.hidden) void this.loadOverview(false); };

  private async loadOverview(first: boolean) {
    try {
      const o = await this.api.overview(30);
      this.o.set(o); this.error.set(''); this.stale.set(false);
      this.updated.set(new Date().toLocaleTimeString('en-GB'));
      if (first) { this.mi.set(o.months.length - 1); const [y, m] = [+o.asOf.slice(0, 4), +o.asOf.slice(5, 7)]; this.calYear = y; this.calMonth = m; }
      await this.loadCalendar();
    } catch (e) { this.stale.set(true); if (!this.o()) this.error.set(messageOf(e)); }
  }

  private async loadCalendar() {
    if (!this.calYear) return;
    try { this.cal.set(await this.api.calendar(this.calYear, this.calMonth)); } catch { /* the rest of the dashboard still works */ }
  }
  protected async stepMonth(d: number) {
    const dt = new Date(this.calYear, this.calMonth - 1 + d, 1); this.calYear = dt.getFullYear(); this.calMonth = dt.getMonth() + 1;
    await this.loadCalendar();
  }

  protected async reloadInsights(refresh: boolean) {
    this.insightsBusy.set(true);
    try { this.insights.set(await this.api.insights(refresh)); } catch { /* the panel keeps its last advice */ } finally { this.insightsBusy.set(false); }
  }

  protected good(k: { kpi: Kpi; invert: boolean }) { return (k.kpi.changePct ?? 0) >= 0 !== k.invert; }
  protected monthOf(c: CustomerHero) { return c.months[Math.min(this.mi(), c.months.length - 1)]; }
  protected frac(v: number, max: number) { return v / max; }
  protected initials(n: string) { return n.split(/\s+/).map(w => w[0]).slice(0, 2).join('').toUpperCase(); }
  protected label(m: number, y: number) { return m === 1 || m === 12 ? `${MONTHS[m - 1]} ’${String(y).slice(2)}` : MONTHS[m - 1]; }
  protected setMonth(i: number) { this.mi.set(i); }
  protected ago(iso: string) {
    const mins = Math.max(0, Math.round((Date.now() - new Date(iso.endsWith('Z') ? iso : iso + 'Z').getTime()) / 60000));
    return mins < 1 ? 'just now' : mins < 60 ? `${mins} min ago` : `${Math.round(mins / 60)} h ago`;
  }

  protected togglePlay() {
    if (this.playing()) { this.stopPlay(); return; }
    const n = this.o()?.months.length ?? 0; if (!n) return;
    if (this.mi() >= n - 1) this.mi.set(0);
    this.playing.set(true);
    const t = setInterval(() => { if (this.mi() >= n - 1) this.stopPlay(); else this.mi.update(v => v + 1); }, 1100);
    this.timers.push(t); this.playTimer = t;
  }
  private playTimer: ReturnType<typeof setInterval> | null = null;
  private stopPlay() { if (this.playTimer) clearInterval(this.playTimer); this.playTimer = null; this.playing.set(false); }
}
