import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { AttendanceDay, AttendanceEvent } from '../../core/models-more';
import { PagedList } from '../../shared/paged-list';
import { DayPipe, Pager, StampTimePipe } from '../../shared/ui';

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const clock = (v: string | null) => (v ? new Date(v.endsWith('Z') ? v : v + 'Z').toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }) : '—');

@Component({
  selector: 'app-attendance-tab',
  imports: [FormsModule, DayPipe, StampTimePipe, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card">
      <div class="toolbar">
        <div class="field inline"><label for="af">From</label><input id="af" class="input" type="date" [ngModel]="from()" (ngModelChange)="setFrom($event)" /></div>
        <div class="field inline"><label for="at">To</label><input id="at" class="input" type="date" [ngModel]="to()" (ngModelChange)="setTo($event)" /></div>
        <div class="tabs pills" role="tablist">
          <button type="button" class="tab" role="tab" [attr.aria-selected]="view() === 'daily'" (click)="view.set('daily')">Per day</button>
          <button type="button" class="tab" role="tab" [attr.aria-selected]="view() === 'events'" (click)="view.set('events')">Every event</button>
        </div>
      </div>
      @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
      @if (view() === 'daily') {
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Date</th><th>Name</th><th>First in</th><th>Last out</th><th class="num">Check-ins</th></tr></thead>
          <tbody>@for (d of days(); track d.userId + d.workDate) {
            <tr><td>{{ d.workDate | day }}</td><td class="strong">{{ d.fullName }}</td><td class="mono">{{ time(d.firstIn) }}</td><td class="mono">{{ time(d.lastOut) }}</td><td class="num mono">{{ d.checkIns }}</td></tr>
          }</tbody>
        </table></div>
        @if (!days().length) { <div class="empty"><strong>No attendance in this period</strong>Clerks check in when they sign in.</div> }
      } @else {
        <div class="table-wrap"><table class="table">
          <thead><tr><th>When</th><th>Name</th><th>Event</th></tr></thead>
          <tbody>@for (e of events.items(); track e.id) { <tr><td>{{ e.at | stampTime }}</td><td class="strong">{{ e.fullName }}</td><td>{{ e.event }}</td></tr> }</tbody>
        </table></div>
        @if (!events.loading() && !events.items().length) { <div class="empty"><strong>No events in this period</strong></div> }
        <app-pager [page]="events.page()" [pageSize]="events.pageSize" [total]="events.total()" (pageChange)="events.goTo($event)" />
      }
    </section>`,
  styles: `.inline { display: flex; align-items: center; gap: .4rem; } .inline label { margin: 0; } .pills { margin: 0 0 0 auto; border: 0; }`,
})
export class AttendanceTab implements OnInit {
  private readonly api = inject(Api2);
  protected readonly from = signal(iso(new Date(Date.now() - 6 * 864e5)));
  protected readonly to = signal(iso(new Date()));
  protected readonly view = signal<'daily' | 'events'>('daily');
  protected readonly days = signal<AttendanceDay[]>([]);
  protected readonly error = signal('');
  protected readonly events = new PagedList<AttendanceEvent>(q => this.api.attendanceEvents({ ...q, from: this.from(), to: this.to() }), 20);
  protected readonly time = clock;

  ngOnInit() { void this.reload(); }
  protected setFrom(v: string) { this.from.set(v); void this.reload(); }
  protected setTo(v: string) { this.to.set(v); void this.reload(); }

  private async reload() {
    if (!this.from() || !this.to()) return;
    try { this.days.set(await this.api.attendanceDaily(this.from(), this.to())); this.error.set(''); } catch (e) { this.error.set(messageOf(e)); }
    this.events.page.set(1); void this.events.load();
  }
}
