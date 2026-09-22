import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { AttendanceTab } from './attendance';
import { EmployeesTab } from './employees';
import { PayrollTab } from './payroll';

type Tab = 'payroll' | 'employees' | 'attendance';

/** Staff office work in one place: who is on the payroll, what is owed this month, and who turned up. Admin only. */
@Component({
  selector: 'app-team',
  imports: [PayrollTab, EmployeesTab, AttendanceTab],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Team</h1></div>
      <div class="tabs" role="tablist">
        @for (t of tabs; track t.id) {
          <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === t.id" (click)="tab.set(t.id)">{{ t.label }}</button>
        }
      </div>
      @switch (tab()) {
        @case ('payroll') { <app-payroll-tab /> }
        @case ('employees') { <app-employees-tab /> }
        @case ('attendance') { <app-attendance-tab /> }
      }
    </div>`,
})
export class TeamPage {
  protected readonly tabs: { id: Tab; label: string }[] = [
    { id: 'payroll', label: 'Monthly payroll' }, { id: 'employees', label: 'Employees & loans' }, { id: 'attendance', label: 'Attendance' },
  ];
  protected readonly tab = signal<Tab>('payroll');
}
