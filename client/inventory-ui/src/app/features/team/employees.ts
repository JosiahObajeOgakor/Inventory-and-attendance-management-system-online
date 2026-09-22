import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { Employee, Loan } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe, NairaPipe } from '../../shared/ui';

@Component({
  selector: 'app-employees-tab',
  imports: [ReactiveFormsModule, Icon, Modal, NairaPipe, DayPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card">
      <div class="toolbar"><div class="grow"></div>
        <button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> New employee</button></div>
      @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
      <div class="table-wrap"><table class="table">
        <thead><tr><th>Employee</th><th>Since</th><th class="num">Monthly salary</th><th class="num">Owes on loans</th><th><span class="sr-only">Actions</span></th></tr></thead>
        <tbody>
          @for (e of employees(); track e.id) {
            <tr [class.off]="!e.isActive">
              <td><span class="strong">{{ e.fullName }}</span>@if (!e.isActive) { <span class="stamp stamp-info off-tag">Not working</span> }<div class="muted sm">{{ e.position }}{{ e.phone ? ' · ' + e.phone : '' }}</div></td>
              <td>{{ e.startedOn | day }}</td><td class="num mono">{{ e.monthlySalary | naira }}</td><td class="num mono" [class.owes]="e.openLoanBalance > 0">{{ e.openLoanBalance | naira }}</td>
              <td class="actions">
                <button type="button" class="btn btn-sm" (click)="openLoans(e)">Loans</button>
                <button type="button" class="btn btn-sm" (click)="openEdit(e)">Edit</button>
                <button type="button" class="btn btn-sm" (click)="toggle(e)">{{ e.isActive ? 'Switch off' : 'Switch on' }}</button>
                @if (!e.payrollMonths && !e.loans) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(e)">Delete</button> }
              </td>
            </tr>
          }
        </tbody>
      </table></div>
      @if (!employees().length) { <div class="empty"><strong>No employees yet</strong>Add the people you pay each month.</div> }
    </section>

    <app-modal [open]="formOpen()" [heading]="editing() ? 'Edit employee' : 'New employee'" (closed)="formOpen.set(false)">
      <form id="ef" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
        <div class="field span-2"><label for="en">Full name</label><input id="en" class="input" formControlName="fullName" /></div>
        <div class="field"><label for="ep">Position</label><input id="ep" class="input" formControlName="position" /></div>
        <div class="field"><label for="eh">Phone</label><input id="eh" class="input" inputmode="tel" formControlName="phone" /></div>
        <div class="field"><label for="es">Monthly salary</label><input id="es" class="input num" type="number" min="0" step="0.01" formControlName="monthlySalary" /></div>
        <div class="field"><label for="ed">Started on</label><input id="ed" class="input" type="date" formControlName="startedOn" /></div>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button>
        <button type="submit" form="ef" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Add employee' }}</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="!!loanFor()" [heading]="'Loans — ' + (loanFor()?.fullName ?? '')" [wide]="true" (closed)="loanFor.set(null)">
      <form class="loan-add" [formGroup]="loanForm" (ngSubmit)="addLoan()" novalidate>
        <div class="field"><label for="la">New loan (₦)</label><input id="la" class="input num" type="number" min="1" step="0.01" formControlName="amount" /></div>
        <div class="field grow"><label for="ln">Note</label><input id="ln" class="input" formControlName="note" /></div>
        <button type="submit" class="btn btn-primary" [disabled]="loanForm.invalid">Give loan</button>
      </form>
      <p class="muted sm">A sixth of what an employee still owes is taken off each month’s pay, until it is cleared.</p>
      @for (l of loans(); track l.id) {
        <article class="loan" [class.closed]="l.closed">
          <header><div><strong class="mono">{{ l.principal | naira }}</strong> <span class="muted">on {{ l.loanDate | day }}</span>{{ l.note ? ' · ' + l.note : '' }}</div>
            <div>@if (l.closed) { <span class="stamp stamp-ok">Cleared</span> } @else { Owes <strong class="mono owes">{{ l.balance | naira }}</strong> }</div></header>
          @for (r of l.repayments; track $index) { <div class="rep"><span>{{ r.paidDate | day }} · {{ r.note ?? 'Repayment' }}</span><span class="mono">{{ r.amount | naira }}</span></div> }
          <footer>
            @if (!l.closed) { <button type="button" class="btn btn-sm" (click)="repay(l)">Record a repayment</button> }
            @if (!l.repayments.length) { <button type="button" class="btn btn-sm btn-danger" (click)="deleteLoan(l)">Delete loan</button> }
          </footer>
        </article>
      } @empty { <div class="empty"><strong>No loans</strong></div> }
    </app-modal>`,
  styles: `
    .sm { font-size: .75rem; } .owes { color: var(--stamp); font-weight: 600; } .off { opacity: .6; } .off-tag { margin-left: .5rem; } .actions { white-space: nowrap; }
    .loan-add { display: flex; gap: .6rem; align-items: end; flex-wrap: wrap; margin-bottom: .5rem; } .loan-add .grow { flex: 1 1 12rem; }
    .loan { border: 1px solid var(--line); border-radius: var(--r-2); padding: .7rem .9rem; margin-top: .7rem; } .loan.closed { opacity: .7; }
    .loan header { display: flex; justify-content: space-between; gap: 1rem; flex-wrap: wrap; } .rep { display: flex; justify-content: space-between; font-size: .8125rem; color: var(--muted); padding: .2rem 0; border-top: 1px dashed var(--line); margin-top: .3rem; }
    .loan footer { display: flex; gap: .5rem; margin-top: .5rem; } .loan footer:empty { display: none; }
  `,
})
export class EmployeesTab implements OnInit {
  private readonly api = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly employees = signal<Employee[]>([]);
  protected readonly error = signal('');
  protected readonly formOpen = signal(false);
  protected readonly editing = signal<Employee | null>(null);
  protected readonly busy = signal(false);
  protected readonly loanFor = signal<Employee | null>(null);
  protected readonly loans = signal<Loan[]>([]);
  protected readonly form = this.fb.nonNullable.group({
    fullName: ['', [Validators.required, Validators.maxLength(120)]], position: ['', [Validators.required, Validators.maxLength(80)]], phone: [''],
    monthlySalary: [0, [Validators.required, Validators.min(0)]], startedOn: [''],
  });
  protected readonly loanForm = this.fb.nonNullable.group({ amount: [0, [Validators.required, Validators.min(1)]], note: [''] });

  ngOnInit() { void this.load(); }
  private async load() { try { this.employees.set(await this.api.employees()); this.error.set(''); } catch (e) { this.error.set(messageOf(e)); } }

  protected openNew() { this.editing.set(null); this.form.reset({ fullName: '', position: '', phone: '', monthlySalary: 0, startedOn: '' }); this.formOpen.set(true); }
  protected openEdit(e: Employee) { this.editing.set(e); this.form.reset({ fullName: e.fullName, position: e.position, phone: e.phone ?? '', monthlySalary: e.monthlySalary, startedOn: e.startedOn }); this.formOpen.set(true); }

  protected async save() {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    const input = { fullName: v.fullName.trim(), position: v.position.trim(), phone: v.phone.trim() || null, startedOn: v.startedOn || null, monthlySalary: Number(v.monthlySalary) };
    this.busy.set(true);
    try {
      const e = this.editing();
      if (e) await this.api.updateEmployee(e.id, input); else await this.api.createEmployee(input);
      this.toasts.ok(e ? 'Employee updated.' : 'Employee added.'); this.formOpen.set(false); await this.load();
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async toggle(e: Employee) {
    try { await this.api.setEmployeeActive(e.id, !e.isActive); await this.load(); } catch (err) { this.toasts.error(messageOf(err)); }
  }

  protected async remove(e: Employee) {
    const ok = await this.confirm.ask({ title: `Delete ${e.fullName}?`, message: 'Employees with pay or loan records are kept — switch them off instead.', confirmLabel: 'Delete employee', danger: true });
    if (ok === null) return;
    try { await this.api.deleteEmployee(e.id); await this.load(); } catch (err) { this.toasts.error(messageOf(err)); }
  }

  protected async openLoans(e: Employee) { this.loanFor.set(e); this.loanForm.reset({ amount: 0, note: '' }); await this.loadLoans(); }
  private async loadLoans() {
    const e = this.loanFor(); if (!e) return;
    try { this.loans.set(await this.api.employeeLoans(e.id)); } catch (err) { this.toasts.error(messageOf(err)); }
    await this.load();
  }

  protected async addLoan() {
    const e = this.loanFor(); if (!e || this.loanForm.invalid) return;
    const v = this.loanForm.getRawValue();
    try { await this.api.addLoan(e.id, Number(v.amount), v.note.trim() || null); this.loanForm.reset({ amount: 0, note: '' }); this.toasts.ok('Loan recorded.'); await this.loadLoans(); } catch (err) { this.toasts.error(messageOf(err)); }
  }

  protected async repay(l: Loan) {
    const text = await this.confirm.ask({ title: 'Record a repayment', message: `Up to ₦${l.balance.toLocaleString('en-NG', { minimumFractionDigits: 2 })} is still owed. Type the amount received.`, confirmLabel: 'Record repayment', reason: { label: 'Amount (₦)', required: true } });
    if (text === null) return;
    const amount = Number(text.replace(/[₦,\s]/g, ''));
    if (!(amount > 0)) { this.toasts.error('Enter the amount as a number greater than zero.'); return; }
    try { await this.api.repayLoan(l.id, amount, null); this.toasts.ok('Repayment recorded.'); await this.loadLoans(); } catch (err) { this.toasts.error(messageOf(err)); }
  }

  protected async deleteLoan(l: Loan) {
    const ok = await this.confirm.ask({ title: 'Delete this loan?', message: 'Only a loan with no repayments can be deleted.', confirmLabel: 'Delete loan', danger: true });
    if (ok === null) return;
    try { await this.api.deleteLoan(l.id); await this.loadLoans(); } catch (err) { this.toasts.error(messageOf(err)); }
  }
}
