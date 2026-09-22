import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { ExpenseList, ExpenseRow } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';
import { DayPipe, NairaPipe, Pager } from '../../shared/ui';

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

@Component({
  selector: 'app-expenses',
  imports: [ReactiveFormsModule, FormsModule, Icon, Modal, NairaPipe, DayPipe, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Expenses</h1>
        <div class="actions">
          <a class="btn" [href]="exportUrl()" download><app-icon name="download" [size]="18" /> Excel</a>
          <button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> Record expense</button>
        </div>
      </div>

      <section class="card">
        <div class="toolbar">
          <div class="field inline"><label for="ef">From</label><input id="ef" class="input" type="date" [ngModel]="from()" (ngModelChange)="setFrom($event)" /></div>
          <div class="field inline"><label for="et">To</label><input id="et" class="input" type="date" [ngModel]="to()" (ngModelChange)="setTo($event)" /></div>
          <div class="field inline"><label for="ec">Category</label>
            <select id="ec" class="input" [ngModel]="category()" (ngModelChange)="setCategory($event)"><option value="">All</option>@for (c of categories(); track c) { <option>{{ c }}</option> }</select></div>
          <div class="search grow"><app-icon name="search" [size]="17" /><input class="input" type="search" placeholder="Search notes" aria-label="Search expenses" (input)="setSearch($any($event.target).value)" /></div>
        </div>
        @if (data(); as d) {
          <div class="sums">
            <div><span class="eyebrow">Spent in this period</span><strong class="figure">{{ d.total | naira }}</strong></div>
            @for (c of d.byCategory; track c.category) { <div><span class="eyebrow">{{ c.category }}</span><span class="mono">{{ c.total | naira }}</span></div> }
          </div>
        }
        @if (error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ error() }}</p> }
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Date</th><th>Category</th><th>Note</th><th class="num">Amount</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (e of data()?.page?.items ?? []; track e.id) {
              <tr><td>{{ e.expenseDate | day }}</td><td class="strong">{{ e.category }}</td><td>{{ e.note ?? '' }}</td><td class="num mono">{{ e.amount | naira }}</td>
                <td class="actions"><button type="button" class="btn btn-sm" (click)="openEdit(e)">Edit</button> <button type="button" class="btn btn-sm btn-danger" (click)="remove(e)">Delete</button></td></tr>
            }
          </tbody>
        </table></div>
        @if (!loading() && !(data()?.page?.items?.length)) { <div class="empty"><strong>No expenses in this period</strong>Rent, power, fuel — record what the business spends.</div> }
        <app-pager [page]="page()" [pageSize]="pageSize" [total]="data()?.page?.total ?? 0" (pageChange)="goTo($event)" />
      </section>
    </div>

    <app-modal [open]="formOpen()" [heading]="editing() ? 'Edit expense' : 'Record expense'" (closed)="formOpen.set(false)">
      <form id="xf" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
        <div class="field"><label for="xc">Category</label><select id="xc" class="input" formControlName="category">@for (c of categories(); track c) { <option>{{ c }}</option> }</select></div>
        <div class="field"><label for="xd">Date</label><input id="xd" class="input" type="date" formControlName="expenseDate" /></div>
        <div class="field"><label for="xa">Amount (₦)</label><input id="xa" class="input num" type="number" min="0.01" step="0.01" formControlName="amount" /></div>
        <div class="field span-2"><label for="xn">Note</label><input id="xn" class="input" formControlName="note" maxlength="250" /></div>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button>
        <button type="submit" form="xf" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Record expense' }}</button>
      </ng-container>
    </app-modal>`,
  styles: `
    .inline { display: flex; align-items: center; gap: .4rem; } .inline label { margin: 0; } .actions { white-space: nowrap; }
    .sums { display: flex; flex-wrap: wrap; gap: 1.5rem; padding: .9rem 1.125rem; border-bottom: 1px solid var(--line); align-items: end; }
    .sums div { display: flex; flex-direction: column; gap: .15rem; } .sums .figure { font-size: 1.6rem; color: var(--brand); }
  `,
})
export class ExpensesPage implements OnInit {
  private readonly api = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly pageSize = 15;
  protected readonly categories = signal<string[]>([]);
  protected readonly from = signal(iso(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly to = signal(iso(new Date()));
  protected readonly category = signal('');
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly data = signal<ExpenseList | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly formOpen = signal(false);
  protected readonly editing = signal<ExpenseRow | null>(null);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.nonNullable.group({ category: ['Rent'], expenseDate: [''], amount: [0, [Validators.required, Validators.min(0.01)]], note: [''] });
  private timer: ReturnType<typeof setTimeout> | null = null;

  async ngOnInit() {
    try { const c = await this.api.expenseCategories(); this.categories.set(c); this.form.controls.category.setValue(c[0] ?? 'Other'); } catch { /* the page still lists */ }
    await this.load();
  }

  protected exportUrl() { return `/api/exports/expenses?from=${this.from()}&to=${this.to()}`; }
  protected setFrom(v: string) { this.from.set(v); this.page.set(1); void this.load(); }
  protected setTo(v: string) { this.to.set(v); this.page.set(1); void this.load(); }
  protected setCategory(v: string) { this.category.set(v); this.page.set(1); void this.load(); }
  protected setSearch(v: string) { this.search.set(v); if (this.timer) clearTimeout(this.timer); this.timer = setTimeout(() => { this.page.set(1); void this.load(); }, 300); }
  protected goTo(p: number) { this.page.set(p); void this.load(); }

  private async load() {
    if (!this.from() || !this.to()) return;
    this.loading.set(true);
    try { this.data.set(await this.api.expenses({ page: this.page(), pageSize: this.pageSize, search: this.search(), from: this.from(), to: this.to(), category: this.category() || undefined })); this.error.set(''); }
    catch (e) { this.error.set(messageOf(e)); } finally { this.loading.set(false); }
  }

  protected openNew() { this.editing.set(null); this.form.reset({ category: this.categories()[0] ?? 'Other', expenseDate: iso(new Date()), amount: 0, note: '' }); this.formOpen.set(true); }
  protected openEdit(e: ExpenseRow) { this.editing.set(e); this.form.reset({ category: e.category, expenseDate: e.expenseDate, amount: e.amount, note: e.note ?? '' }); this.formOpen.set(true); }

  protected async save() {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    const input = { category: v.category, expenseDate: v.expenseDate || null, amount: Number(v.amount), note: v.note.trim() || null };
    this.busy.set(true);
    try {
      const e = this.editing();
      if (e) await this.api.updateExpense(e.id, input); else await this.api.createExpense(input);
      this.toasts.ok(e ? 'Expense updated.' : 'Expense recorded.'); this.formOpen.set(false); await this.load();
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async remove(e: ExpenseRow) {
    const ok = await this.confirm.ask({ title: 'Delete this expense?', message: `${e.category} of ₦${e.amount.toLocaleString('en-NG', { minimumFractionDigits: 2 })}. Profit figures will change.`, confirmLabel: 'Delete expense', danger: true });
    if (ok === null) return;
    try { await this.api.deleteExpense(e.id); await this.load(); } catch (err) { this.toasts.error(messageOf(err)); }
  }
}
