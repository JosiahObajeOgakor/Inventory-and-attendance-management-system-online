import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Employee } from '../../core/models-more';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { AppsApi, LeaveType, TimeOff } from './apps-api';

const STATUSES = ['', 'Pending', 'Approved', 'Rejected', 'Cancelled'];
const stamp = (s: string) => s === 'Approved' ? 'ok' : s === 'Pending' ? 'warn' : s === 'Rejected' ? 'bad' : 'info';

/** Time Off: who is away, requests waiting, balances, and the leave types. */
@Component({
  selector: 'app-time-off',
  imports: [FormsModule, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Time Off</h1><div class="grow"></div>
        @if (auth.isAdmin()) { <button type="button" class="btn" (click)="typesOpen.set(true)">Leave types</button><button type="button" class="btn btn-primary" (click)="startRequest()">Record leave</button> }</div>

      @if (d(); as t) {
        <div class="kpis">
          <div class="card kpi" [class.warn]="t.pendingCount > 0"><span class="eyebrow">Waiting for approval</span><div class="value">{{ t.pendingCount }}</div></div>
          <div class="card kpi"><span class="eyebrow">Away today</span><div class="value">{{ t.awayToday.length }}</div>
            <div class="sub">@for (a of t.awayToday; track a.employeeId; let last = $last) { {{ a.employee }} ({{ a.leaveType }}, back after {{ a.toDate }}){{ last ? '' : '; ' }} } @empty { Everyone is in }</div></div>
        </div>

        <div class="tabs" role="tablist">
          <button type="button" role="tab" [attr.aria-selected]="tab() === 'requests'" [class.on]="tab() === 'requests'" (click)="tab.set('requests')">Requests</button>
          <button type="button" role="tab" [attr.aria-selected]="tab() === 'balances'" [class.on]="tab() === 'balances'" (click)="tab.set('balances')">Balances {{ t.year }}</button>
          <button type="button" role="tab" [attr.aria-selected]="tab() === 'unpaid'" [class.on]="tab() === 'unpaid'" (click)="openUnpaid()">Unpaid leave for payroll</button>
        </div>

        @if (tab() === 'requests') {
          <section class="card">
            <div class="toolbar"><select class="input" aria-label="Status" [ngModel]="status()" (ngModelChange)="filter($event)">@for (s of statuses; track s) { <option [ngValue]="s">{{ s || 'All requests' }}</option> }</select></div>
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Employee</th><th>Leave</th><th>From</th><th>To</th><th class="num">Days</th><th>Status</th><th>Recorded by</th><th></th></tr></thead>
              <tbody>@for (r of t.requests; track r.id) {
                <tr><td class="strong">{{ r.employee }}</td><td>{{ r.leaveType }} @if (!r.paid) { <span class="muted sm">unpaid</span> }</td><td>{{ r.fromDate }}</td><td>{{ r.toDate }}</td><td class="num">{{ r.days }}</td>
                  <td><span [class]="'stamp stamp-' + tone(r.status)">{{ r.status }}</span>@if (r.decisionNote) { <div class="muted sm">{{ r.decisionNote }}</div> }</td>
                  <td class="muted sm">{{ r.requestedBy }}</td>
                  <td class="actions">
                    @if (r.status === 'Pending' && auth.isAdmin()) { <button type="button" class="btn btn-sm btn-primary" (click)="approve(r.id)">Approve</button><button type="button" class="btn btn-sm" (click)="reject(r.id)">Reject</button> }
                    @if (r.status === 'Pending' || (r.status === 'Approved' && r.fromDate > today)) { <button type="button" class="btn btn-sm" (click)="cancel(r.id)">Cancel</button> }
                  </td></tr>
              }</tbody>
            </table></div>
            @if (!t.requests.length) { <div class="empty"><strong>No leave recorded</strong>Record leave for an employee; someone else then approves it.</div> }
          </section>
        }
        @if (tab() === 'balances') {
          <section class="card"><div class="table-wrap"><table class="table">
            <thead><tr><th>Employee</th><th>Leave</th><th class="num">Entitled</th><th class="num">Taken</th><th class="num">Waiting</th><th class="num">Left</th></tr></thead>
            <tbody>@for (b of t.balances; track b.employeeId + '-' + b.leaveTypeId) {
              <tr><td>{{ b.employee }}</td><td>{{ b.leaveType }}</td><td class="num">{{ b.entitled }}</td><td class="num">{{ b.taken }}</td><td class="num">{{ b.pending }}</td><td class="num strong">{{ b.left }}</td></tr>
            }</tbody></table></div>
            @if (!t.balances.length) { <div class="empty"><strong>No balances yet</strong>Add employees under People, and give a leave type days a year.</div> }
          </section>
        }
        @if (tab() === 'unpaid') {
          <section class="card">
            <div class="toolbar"><input class="input" type="month" aria-label="Month" [ngModel]="month()" (ngModelChange)="pickMonth($event)" />
              <span class="muted sm">Approved unpaid leave, in working days, for payroll to deduct.</span></div>
            <div class="table-wrap"><table class="table"><thead><tr><th>Employee</th><th class="num">Unpaid days</th></tr></thead>
              <tbody>@for (u of unpaid(); track u.employeeId) { <tr><td>{{ u.employee }}</td><td class="num strong">{{ u.days }}</td></tr> }</tbody></table></div>
            @if (!unpaid().length) { <div class="empty"><strong>No unpaid leave this month</strong></div> }
          </section>
        }
      }
    </div>

    <app-modal [open]="requesting()" heading="Record leave" (closed)="requesting.set(false)">
      <form id="lr" class="form-grid" (ngSubmit)="saveRequest()">
        <div class="field span-2"><label for="le">Employee</label><select id="le" class="input" name="le" [(ngModel)]="r.employeeId" required><option [ngValue]="0">Choose…</option>@for (e of employees(); track e.id) { <option [ngValue]="e.id">{{ e.fullName }}</option> }</select></div>
        <div class="field span-2"><label for="lt">Leave type</label><select id="lt" class="input" name="lt" [(ngModel)]="r.leaveTypeId" required>@for (t of activeTypes(); track t.id) { <option [ngValue]="t.id">{{ t.name }}{{ t.daysPerYear ? ' (' + t.daysPerYear + ' days a year)' : '' }}</option> }</select></div>
        <div class="field"><label for="lf">First day</label><input id="lf" class="input" type="date" name="lf" [(ngModel)]="r.fromDate" required /></div>
        <div class="field"><label for="ll">Last day</label><input id="ll" class="input" type="date" name="ll" [(ngModel)]="r.toDate" required /></div>
        <div class="field span-2"><label for="lo">Reason</label><input id="lo" class="input" name="lo" [(ngModel)]="r.reason" /></div>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="requesting.set(false)">Cancel</button>
        <button type="submit" form="lr" class="btn btn-primary" [disabled]="!r.employeeId || !r.fromDate || !r.toDate">Record</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="typesOpen()" heading="Leave types" (closed)="typesOpen.set(false)">
      <table class="table"><thead><tr><th>Name</th><th class="num">Days a year</th><th>Paid</th><th>On</th><th></th></tr></thead>
        <tbody>@for (t of editTypes(); track $index) {
          <tr><td><input class="input" [(ngModel)]="t.name" [attr.aria-label]="'Name'" /></td><td><input class="input num" type="number" min="0" [(ngModel)]="t.daysPerYear" aria-label="Days a year" /></td>
            <td><input type="checkbox" [(ngModel)]="t.paid" aria-label="Paid" /></td><td><input type="checkbox" [(ngModel)]="t.isActive" aria-label="On" /></td>
            <td><button type="button" class="btn btn-sm" (click)="saveType(t)">Save</button></td></tr>
        }</tbody></table>
      <p class="muted sm">0 days means no limit. These starting allowances are only a guide: set them to your own policy.</p>
      <button type="button" class="btn" (click)="addType()">Add a leave type</button>
      <ng-container modal-actions><button type="button" class="btn btn-primary" (click)="typesOpen.set(false)">Done</button></ng-container>
    </app-modal>`,
  styles: `.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr)); gap: .8rem; margin-bottom: 1rem; } .page-head { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; }
    .tabs { display: flex; gap: .3rem; margin-bottom: .8rem; flex-wrap: wrap; } .tabs button { border: 1px solid var(--line, #dde1ea); background: var(--paper, #fff); padding: .45rem .9rem; border-radius: 999px; cursor: pointer; font: inherit; }
    .tabs button.on { background: var(--brand, #0f1e3d); color: #fff; border-color: var(--brand, #0f1e3d); } .sm { font-size: .78rem; } .actions { white-space: nowrap; }`,
})
export class TimeOffPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(AppsApi);
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly statuses = STATUSES;
  protected readonly today = new Date().toISOString().slice(0, 10);
  protected readonly d = signal<TimeOff | null>(null);
  protected readonly tab = signal<'requests' | 'balances' | 'unpaid'>('requests');
  protected readonly status = signal('');
  protected readonly employees = signal<Employee[]>([]);
  protected readonly requesting = signal(false);
  protected readonly typesOpen = signal(false);
  protected readonly unpaid = signal<{ employeeId: number; employee: string; days: number }[]>([]);
  protected readonly month = signal(new Date().toISOString().slice(0, 7));
  protected readonly activeTypes = computed(() => (this.d()?.types ?? []).filter(t => t.isActive));
  protected readonly editTypes = signal<LeaveType[]>([]);
  protected r = { employeeId: 0, leaveTypeId: 0, fromDate: '', toDate: '', reason: '' };
  protected tone = stamp;

  ngOnInit() { void this.load(); void this.api2.employees().then(e => this.employees.set(e.filter(x => x.isActive))).catch(() => {}); }
  private async load() {
    try { const t = await this.api.timeOff(undefined, this.status() || undefined); this.d.set(t); this.editTypes.set(t.types.map(x => ({ ...x }))); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected filter(s: string) { this.status.set(s); void this.load(); }
  protected startRequest() { this.r = { employeeId: 0, leaveTypeId: this.activeTypes()[0]?.id ?? 0, fromDate: '', toDate: '', reason: '' }; this.requesting.set(true); }
  protected async saveRequest() {
    try { await this.api.requestLeave({ ...this.r, reason: this.r.reason.trim() || null }); this.requesting.set(false); this.toasts.ok('Leave recorded. It now waits for approval.'); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async approve(id: number) { try { await this.api.decideLeave(id, true, null); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async reject(id: number) {
    const why = await this.confirm.ask({ title: 'Reject this leave?', message: 'The reason is kept with the request.', confirmLabel: 'Reject', reason: { label: 'Reason', required: true } });
    if (why === null) return;
    try { await this.api.decideLeave(id, false, why); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async cancel(id: number) {
    if ((await this.confirm.ask({ title: 'Cancel this leave?', message: 'The days go back into the balance.', confirmLabel: 'Cancel leave', danger: true })) === null) return;
    try { await this.api.cancelLeave(id); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected addType() { this.editTypes.update(l => [...l, { id: 0, name: '', daysPerYear: 0, paid: true, isActive: true }]); }
  protected async saveType(t: LeaveType) {
    try { await this.api.saveLeaveType({ id: t.id || null, name: t.name, daysPerYear: Number(t.daysPerYear) || 0, paid: t.paid, isActive: t.isActive }); this.toasts.ok('Saved.'); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected openUnpaid() { this.tab.set('unpaid'); void this.pickMonth(this.month()); }
  protected async pickMonth(m: string) {
    this.month.set(m); const [y, mo] = m.split('-').map(Number); if (!y || !mo) return;
    try { this.unpaid.set(await this.api.unpaidLeave(y, mo)); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
