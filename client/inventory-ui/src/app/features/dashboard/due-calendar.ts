import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CalendarData, DueItem } from '../../core/models-dash';
import { Icon } from '../../shared/icon';
import { compact, naira } from './widgets';

interface Cell { date: string; day: number; inMonth: boolean; today: boolean; items: DueItem[]; col: number; }

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const iso = (y: number, m: number, d: number) => `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
const longDay = (s: string) => new Date(s + 'T00:00:00').toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' });

/**
 * Who owes what, and when it falls due, scattered across the month. Hover (or focus) a date to read the detail:
 * "Josiah owes ₦4,000 and it is due for payment on Monday 22 September."
 */
@Component({
  selector: 'app-due-calendar',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <button type="button" class="nav" (click)="step.emit(-1)" aria-label="Previous month"><app-icon name="chevron" [size]="16" class="flip" /></button>
      <strong class="month">{{ title() }}</strong>
      <button type="button" class="nav" (click)="step.emit(1)" aria-label="Next month"><app-icon name="chevron" [size]="16" /></button>
      <span class="legend"><i class="o"></i>Overdue <i class="s"></i>Due this week <i class="u"></i>Upcoming</span>
    </div>
    <div class="grid" role="grid" [attr.aria-label]="'Payments due in ' + title()">
      @for (d of dow; track d) { <div class="dow" role="columnheader">{{ d }}</div> }
      @for (c of cells(); track c.date) {
        <div class="cell" role="gridcell" [class.out]="!c.inMonth" [class.today]="c.today" [class.has]="c.items.length" [attr.tabindex]="c.items.length ? 0 : -1"
             [attr.aria-label]="c.items.length ? label(c) : null" (mouseenter)="show(c)" (mouseleave)="hide()" (focus)="show(c)" (blur)="hide()">
          <span class="n">{{ c.day }}</span>
          @for (it of c.items.slice(0, 2); track it.invoiceId) {
            <span class="pill" [class]="kind(it)">{{ short(it) }}</span>
          }
          @if (c.items.length > 2) { <span class="more">+{{ c.items.length - 2 }} more</span> }
          @if (open()?.date === c.date && c.items.length) {
            <div class="tip" [class.right]="c.col >= 4" role="tooltip">
              <div class="when">{{ longDay(c.date) }}</div>
              @for (it of c.items; track it.invoiceId) {
                <p><i [class]="kind(it)"></i><span><strong>{{ it.customer }}</strong> owes <b>{{ full(it.outstanding) }}</b> and it is due for payment on {{ longDay(it.dueDate) }}.
                  @if (it.daysOverdue > 0) { <em>{{ it.daysOverdue }} day{{ it.daysOverdue === 1 ? '' : 's' }} overdue.</em> }
                  <small>{{ it.invoiceNumber }}{{ it.phone ? ' · ' + it.phone : '' }}</small></span></p>
              }
            </div>
          }
        </div>
      }
    </div>
    @if (earlier().length) {
      <div class="earlier" tabindex="0" (mouseenter)="showEarlier.set(true)" (mouseleave)="showEarlier.set(false)" (focus)="showEarlier.set(true)" (blur)="showEarlier.set(false)">
        <span class="pill overdue">Overdue</span> {{ earlier().length }} earlier invoice(s) still unpaid — {{ full(earlierTotal()) }}
        @if (showEarlier()) {
          <div class="tip up" role="tooltip">@for (it of earlier().slice(0, 6); track it.invoiceId) {
            <p><i class="overdue"></i><span><strong>{{ it.customer }}</strong> owes <b>{{ full(it.outstanding) }}</b>, due {{ longDay(it.dueDate) }}. <em>{{ it.daysOverdue }} days overdue.</em></span></p>
          }</div>
        }
      </div>
    }`,
  styles: `
    :host { display: block; } .head { display: flex; align-items: center; gap: .6rem; margin-bottom: .8rem; flex-wrap: wrap; } .month { min-width: 9rem; text-align: center; font-size: .95rem; }
    .nav { width: 30px; height: 30px; border-radius: 9px; border: 1px solid #e3e8f0; background: #fff; display: grid; place-items: center; cursor: pointer; color: #4a5468; } .nav:hover { background: #f3f6fb; } .flip { transform: scaleX(-1); }
    .legend { margin-left: auto; font-size: .75rem; color: #7a8497; display: flex; align-items: center; gap: .35rem; } .legend i { width: 9px; height: 9px; border-radius: 50%; display: inline-block; margin-left: .55rem; }
    .o, .overdue > i, i.overdue { background: #ef5b52; } .s, i.soon { background: #f5a524; } .u, i.upcoming { background: #3db4ff; }
    .grid { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); border-top: 1px solid #edf0f5; border-left: 1px solid #edf0f5; }
    .dow { padding: .4rem .5rem; font-size: .6875rem; letter-spacing: .06em; text-transform: uppercase; color: #8b95a7; border-right: 1px solid #edf0f5; border-bottom: 1px solid #edf0f5; background: #fafbfd; }
    .cell { position: relative; min-height: 86px; padding: .3rem .35rem; border-right: 1px solid #edf0f5; border-bottom: 1px solid #edf0f5; display: flex; flex-direction: column; gap: 3px; background: #fff; }
    .cell.out { background: #fafbfd; } .cell.out .n { color: #c3cad6; } .cell.has { cursor: default; } .cell.has:hover, .cell.has:focus-visible { background: #f3f9ff; outline: none; z-index: 5; box-shadow: inset 0 0 0 2px #3db4ff; }
    .n { font-size: .75rem; color: #5b6579; } .cell.today .n { background: #1c2130; color: #fff; border-radius: 50%; width: 22px; height: 22px; display: grid; place-items: center; }
    .pill { font-size: .6875rem; padding: 2px 6px; border-radius: 6px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-weight: 600; }
    .pill.overdue { background: #fdeceb; color: #b3261e; } .pill.soon { background: #fff3dc; color: #8a5a00; } .pill.upcoming { background: #e4f4ff; color: #0b6aa8; } .more { font-size: .6875rem; color: #7a8497; }
    .tip { position: absolute; z-index: 20; top: 100%; left: 8px; width: 280px; background: #1c2130; color: #fff; border-radius: 12px; padding: .7rem .8rem; box-shadow: 0 12px 32px rgb(20 30 60 / .28); font-size: .78rem; line-height: 1.45; }
    .tip.right { left: auto; right: 8px; } .tip.up { top: auto; bottom: 110%; left: 0; } .when { font-weight: 700; margin-bottom: .35rem; color: #9ad9ff; }
    .tip p { margin: .35rem 0 0; display: flex; gap: .5rem; } .tip i { flex: none; width: 8px; height: 8px; border-radius: 50%; margin-top: .4rem; } .tip b { font-family: var(--font-mono); } .tip em { color: #ffb4ae; font-style: normal; display: block; }
    .tip small { display: block; color: #9aa6bd; font-family: var(--font-mono); font-size: .68rem; }
    .earlier { position: relative; margin-top: .7rem; padding: .55rem .7rem; background: #fdf4f3; border-radius: 10px; font-size: .8125rem; color: #6d2320; cursor: default; } .earlier:focus-visible { outline: 2px solid #ef5b52; }
    @media (max-width: 700px) { .cell { min-height: 64px; } .pill { font-size: .6rem; } .legend { display: none; } }
  `,
})
export class DueCalendar {
  readonly data = input.required<CalendarData>();
  readonly today = input.required<string>();
  readonly step = output<number>();
  protected readonly dow = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  protected readonly open = signal<Cell | null>(null);
  protected readonly showEarlier = signal(false);
  protected readonly longDay = longDay;

  protected readonly title = computed(() => `${MONTHS[this.data().month - 1]} ${this.data().year}`);
  protected readonly cells = computed<Cell[]>(() => {
    const { year, month, due } = this.data();
    const first = new Date(year, month - 1, 1); const lead = (first.getDay() + 6) % 7;
    const days = new Date(year, month, 0).getDate();
    const by = new Map<string, DueItem[]>(); for (const d of due) by.set(d.dueDate, [...(by.get(d.dueDate) ?? []), d]);
    const total = Math.ceil((lead + days) / 7) * 7;
    return Array.from({ length: total }, (_, i) => {
      const dt = new Date(year, month - 1, 1 - lead + i); const s = iso(dt.getFullYear(), dt.getMonth() + 1, dt.getDate());
      return { date: s, day: dt.getDate(), inMonth: dt.getMonth() === month - 1, today: s === this.today(), items: by.get(s) ?? [], col: i % 7 };
    });
  });
  protected readonly earlier = computed(() => {
    const start = iso(this.data().year, this.data().month, 1);
    return this.data().overdue.filter(o => o.dueDate < start);
  });
  protected readonly earlierTotal = computed(() => this.earlier().reduce((s, o) => s + o.outstanding, 0));

  protected kind(it: DueItem): string {
    if (it.dueDate < this.today()) return 'overdue';
    return (new Date(it.dueDate).getTime() - new Date(this.today()).getTime()) / 864e5 <= 7 ? 'soon' : 'upcoming';
  }
  protected short(it: DueItem) { return `${it.customer.split(' ')[0]} · ${compact(it.outstanding)}`; }
  protected full(v: number) { return naira(v); }
  protected label(c: Cell) { return c.items.map(i => `${i.customer} owes ${naira(i.outstanding)}, due ${longDay(i.dueDate)}`).join('. '); }
  protected show(c: Cell) { if (c.items.length) this.open.set(c); }
  protected hide() { this.open.set(null); }
}
