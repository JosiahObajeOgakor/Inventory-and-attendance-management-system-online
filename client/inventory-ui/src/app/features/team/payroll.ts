import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { PayrollRow } from '../../core/models-more';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe, NairaPipe, Stamp } from '../../shared/ui';

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

@Component({
  selector: 'app-payroll-tab',
  imports: [ReactiveFormsModule, Modal, NairaPipe, DayPipe, Stamp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card">
      <div class="toolbar">
        <div class="field inline"><label for="pm">Month</label>
          <select id="pm" class="input" [value]="month()" (change)="setMonth($any($event.target).value)">@for (m of months; track $index) { <option [value]="$index + 1">{{ m }}</option> }</select>
          <input class="input year" type="number" aria-label="Year" [value]="year()" (change)="setYear($any($event.target).value)" /></div>
        <div class="grow"></div>
        <button type="button" class="btn" (click)="generate()">Add everyone for {{ months[month() - 1] }}</button>
        <button type="button" class="btn btn-primary" [disabled]="!unpaid().length" (click)="payAll()">Pay all ({{ unpaid().length }})</button>
      </div>
      @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
      <div class="table-wrap"><table class="table">
        <thead><tr><th>Employee</th><th class="num">Salary</th><th class="num">Loan deduction</th><th class="num">Net pay</th><th>Status</th><th><span class="sr-only">Actions</span></th></tr></thead>
        <tbody>
          @for (r of rows(); track r.id) {
            <tr>
              <td><span class="strong">{{ r.fullName }}</span><div class="muted sm">{{ r.position }}{{ r.note ? ' · ' + r.note : '' }}</div></td>
              <td class="num mono">{{ r.salaryAmount | naira }}</td><td class="num mono">{{ r.loanDeduction | naira }}</td><td class="num mono strong">{{ r.netPay | naira }}</td>
              <td>@if (r.paid) { <app-stamp label="Paid" /><div class="muted sm">{{ r.paidDate | day }}</div> } @else { <app-stamp label="Pending" /> }</td>
              <td class="actions">
                @if (!r.paid) {
                  <button type="button" class="btn btn-sm" (click)="openEdit(r)">Edit</button>
                  <button type="button" class="btn btn-sm btn-primary" (click)="pay(r)">Pay</button>
                  <button type="button" class="btn btn-sm btn-danger" (click)="remove(r)">Remove</button>
                }
              </td>
            </tr>
          }
        </tbody>
        @if (rows().length) {
          <tfoot><tr><th>Total</th><th class="num mono">{{ totals().salary | naira }}</th><th class="num mono">{{ totals().loan | naira }}</th><th class="num mono">{{ totals().net | naira }}</th><th></th><th></th></tr></tfoot>
        }
      </table></div>
      @if (!loading() && !rows().length) { <div class="empty"><strong>No payroll for {{ months[month() - 1] }} {{ year() }} yet</strong>Press “Add everyone” to create a row for each active employee.</div> }
    </section>

    <app-modal [open]="!!editing()" heading="Edit this month’s pay" (closed)="editing.set(null)">
      @if (editing(); as r) {
        <p><strong>{{ r.fullName }}</strong> · {{ months[r.periodMonth - 1] }} {{ r.periodYear }}</p>
        <form id="pe" [formGroup]="form" (ngSubmit)="saveEdit()" class="form-grid" novalidate>
          <div class="field"><label for="ps">Salary</label><input id="ps" class="input num" type="number" min="0" step="0.01" formControlName="salaryAmount" /></div>
          <div class="field"><label for="pl">Loan deduction</label><input id="pl" class="input num" type="number" min="0" step="0.01" formControlName="loanDeduction" /></div>
          <div class="field span-2"><label for="pn">Note</label><input id="pn" class="input" formControlName="note" /></div>
        </form>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="editing.set(null)">Cancel</button>
        <button type="submit" form="pe" class="btn btn-primary" [disabled]="form.invalid">Save</button>
      </ng-container>
    </app-modal>`,
  styles: `.inline { display: flex; align-items: center; gap: .4rem; } .inline label { margin: 0; } .year { width: 6rem; } .sm { font-size: .75rem; } .actions { white-space: nowrap; } tfoot th { border-top: 2px solid var(--ink); }`,
})
export class PayrollTab implements OnInit {
  private readonly api = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly months = MONTHS;
  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly rows = signal<PayrollRow[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly editing = signal<PayrollRow | null>(null);
  protected readonly unpaid = computed(() => this.rows().filter(r => !r.paid));
  protected readonly totals = computed(() => this.rows().reduce((t, r) => ({ salary: t.salary + r.salaryAmount, loan: t.loan + r.loanDeduction, net: t.net + r.netPay }), { salary: 0, loan: 0, net: 0 }));
  protected readonly form = this.fb.nonNullable.group({ salaryAmount: [0, [Validators.required, Validators.min(0)]], loanDeduction: [0, [Validators.required, Validators.min(0)]], note: [''] });

  ngOnInit() { void this.load(); }
  protected setMonth(v: string) { this.month.set(Number(v)); void this.load(); }
  protected setYear(v: string) { const y = Number(v); if (y >= 2000 && y <= 2100) { this.year.set(y); void this.load(); } }

  private async load() {
    this.loading.set(true);
    try { this.rows.set(await this.api.payrollMonth(this.year(), this.month())); this.error.set(''); } catch (e) { this.error.set(messageOf(e)); } finally { this.loading.set(false); }
  }

  protected async generate() {
    try {
      const { added } = await this.api.generatePayroll(this.year(), this.month());
      this.toasts.ok(added ? `Added ${added} employee${added === 1 ? '' : 's'}.` : 'Everyone active already has a row for this month.'); await this.load();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected openEdit(r: PayrollRow) { this.form.reset({ salaryAmount: r.salaryAmount, loanDeduction: r.loanDeduction, note: r.note ?? '' }); this.editing.set(r); }
  protected async saveEdit() {
    const r = this.editing(); if (!r || this.form.invalid) return;
    const v = this.form.getRawValue();
    try { await this.api.editPayrollRow(r.id, { salaryAmount: Number(v.salaryAmount), loanDeduction: Number(v.loanDeduction), note: v.note.trim() || null }); this.editing.set(null); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async pay(r: PayrollRow) {
    const ok = await this.confirm.ask({ title: `Pay ${r.fullName}?`, message: `Net pay is ₦${r.netPay.toLocaleString('en-NG', { minimumFractionDigits: 2 })}. It is recorded as a Salaries expense${r.loanDeduction > 0 ? ' and the loan deduction is taken off their loan' : ''}. This can’t be undone.`, confirmLabel: 'Mark as paid' });
    if (ok === null) return;
    try { await this.api.payRow(r.id); this.toasts.ok(`${r.fullName} paid.`); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async payAll() {
    const ok = await this.confirm.ask({ title: 'Pay everyone still pending?', message: `${this.unpaid().length} people will be marked paid and their net pay recorded as expenses. This can’t be undone.`, confirmLabel: 'Pay all' });
    if (ok === null) return;
    try { const { paid } = await this.api.payAll(this.year(), this.month()); this.toasts.ok(`${paid} paid.`); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async remove(r: PayrollRow) {
    const ok = await this.confirm.ask({ title: `Remove ${r.fullName}’s row?`, message: 'Only rows that have not been paid can be removed.', confirmLabel: 'Remove row', danger: true });
    if (ok === null) return;
    try { await this.api.deletePayrollRow(r.id); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
