import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { NairaPipe } from '../../shared/ui';
import { MONTH_NAMES } from '../accounting/erp-api';
import { Branch, BudgetDetail, BudgetSummary, Erp2Api, Variance } from './erp2-api';

/** Budgets by account and month, and budget against actual. */
@Component({
  selector: 'app-budgets',
  imports: [FormsModule, Modal, NairaPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Budgets</h1><div class="grow"></div>
        @if (auth.can('gl.journal.create')) { <button type="button" class="btn btn-primary" (click)="creating.set(true)">New budget</button> }</div>

      <section class="card">
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Budget</th><th>Year</th><th>For</th><th class="num">Income</th><th class="num">Expenses</th><th class="num">Profit</th><th>Status</th><th></th></tr></thead>
          <tbody>@for (b of list(); track b.id) {
            <tr [class.on]="open()?.summary?.id === b.id"><td class="strong">{{ b.name }}</td><td>{{ b.year }}</td><td>{{ branchName(b.branchId) }}</td>
              <td class="num mono">{{ b.income | naira }}</td><td class="num mono">{{ b.expenses | naira }}</td><td class="num mono strong">{{ b.profit | naira }}</td>
              <td><span class="stamp" [class]="'stamp stamp-' + (b.status === 'Approved' ? 'ok' : 'warn')">{{ b.status }}</span></td>
              <td class="actions"><button type="button" class="btn btn-sm" (click)="show(b.id)">Edit</button><button type="button" class="btn btn-sm" (click)="showVariance(b.id)">vs actual</button></td></tr>
          }</tbody>
        </table></div>
        @if (!list().length) { <div class="empty"><strong>No budgets yet</strong>Start one from last year's actuals plus growth, then adjust.</div> }
      </section>

      @if (open(); as d) {
        <section class="card">
          <div class="toolbar"><h2 class="h4">{{ d.summary.name }} · {{ d.summary.year }}</h2><div class="grow"></div>
            @if (d.summary.status === 'Draft') {
              @if (auth.can('gl.journal.create')) { <button type="button" class="btn" (click)="spreadAll()">Spread totals evenly</button><button type="button" class="btn btn-primary" (click)="saveRows()">Save</button> }
              @if (auth.can('gl.journal.approve')) { <button type="button" class="btn" (click)="approve(true)">Approve</button> }
              @if (auth.can('gl.journal.create')) { <button type="button" class="btn btn-danger" (click)="remove()">Delete</button> }
            } @else if (auth.can('gl.journal.approve')) { <button type="button" class="btn" (click)="approve(false)">Reopen</button> }
          </div>
          <div class="table-wrap grid"><table class="table compact">
            <thead><tr><th>Account</th>@for (m of shortMonths; track $index) { <th class="num">{{ m }}</th> }<th class="num">Year</th></tr></thead>
            <tbody>@for (r of d.rows; track r.code) {
              <tr [class.inc]="r.type === 'Income'"><td class="acct">{{ r.code }} {{ r.account }}</td>
                @for (m of r.months; track $index; let i = $index) {
                  <td class="num"><input class="cell" type="number" [disabled]="d.summary.status !== 'Draft'" [ngModel]="r.months[i]" (ngModelChange)="setCell(r.code, i, $event)" [attr.aria-label]="r.account + ' ' + shortMonths[i]" /></td>
                }
                <td class="num mono strong">{{ rowTotal(r.code) | naira }}</td></tr>
            }</tbody>
          </table></div>
        </section>
      }

      @if (vari(); as v) {
        <section class="card">
          <div class="toolbar"><h2 class="h4">{{ v.name }}: budget against actual, January – {{ months[v.toMonth - 1] }} {{ v.year }}</h2><div class="grow"></div>
            <select class="input" aria-label="To month" [ngModel]="v.toMonth" (ngModelChange)="showVariance(v.budgetId, +$event)">@for (m of months; track $index) { <option [ngValue]="$index + 1">{{ m }}</option> }</select></div>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Account</th><th class="num">Budget</th><th class="num">Actual</th><th class="num">Variance</th><th class="num">%</th></tr></thead>
            <tbody>@for (r of v.rows; track r.code) {
              <tr><td>{{ r.code }} {{ r.account }} <span class="muted sm">{{ r.type }}</span></td><td class="num mono">{{ r.budget | naira }}</td><td class="num mono">{{ r.actual | naira }}</td>
                <td class="num mono" [class.good]="r.favourable" [class.bad]="!r.favourable">{{ r.variance | naira }}</td><td class="num">{{ r.variancePct === null ? '—' : r.variancePct + '%' }}</td></tr>
            }</tbody>
            <tfoot><tr><th>Profit</th><th class="num mono">{{ v.budgetProfit | naira }}</th><th class="num mono">{{ v.actualProfit | naira }}</th>
              <th class="num mono" [class.good]="v.actualProfit >= v.budgetProfit" [class.bad]="v.actualProfit < v.budgetProfit">{{ v.actualProfit - v.budgetProfit | naira }}</th><th></th></tr></tfoot>
          </table></div>
        </section>
      }
    </div>

    <app-modal [open]="creating()" heading="New budget" (closed)="creating.set(false)">
      <form id="nb" class="form-grid" (ngSubmit)="create()">
        <div class="field span-2"><label for="nn">Name</label><input id="nn" class="input" name="nn" [(ngModel)]="n.name" placeholder="e.g. Operating budget" /></div>
        <div class="field"><label for="ny">Year</label><input id="ny" class="input" type="number" name="ny" [(ngModel)]="n.year" /></div>
        <div class="field"><label for="nr">For</label><select id="nr" class="input" name="nr" [(ngModel)]="n.branchId"><option [ngValue]="null">Whole business</option>@for (b of branches(); track b.id) { <option [ngValue]="b.id">{{ b.name }}</option> }</select></div>
        <div class="field"><label for="ns">Start from</label><select id="ns" class="input" name="ns" [(ngModel)]="n.start"><option value="last-year">Last year's actuals</option><option value="empty">Empty</option></select></div>
        @if (n.start === 'last-year') { <div class="field"><label for="ng">Growth %</label><input id="ng" class="input num" type="number" name="ng" [(ngModel)]="n.growthPct" /></div> }
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="creating.set(false)">Cancel</button>
        <button type="submit" form="nb" class="btn btn-primary" [disabled]="!n.name">Create</button>
      </ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; } .page-head { display: flex; gap: .6rem; align-items: center; } .actions { white-space: nowrap; } tr.on td { background: var(--sack); }
    .grid { max-height: 65vh; overflow: auto; } .compact td, .compact th { padding: .2rem .3rem; } .acct { white-space: nowrap; font-size: .8rem; position: sticky; left: 0; background: var(--paper, #fff); }
    .cell { width: 6.5rem; text-align: right; border: 1px solid var(--line); border-radius: 6px; padding: .2rem .35rem; font: inherit; font-size: .8rem; } tr.inc .acct { color: var(--ok, #0f7a6d); }
    .good { color: var(--ok, #0f7a6d); } .bad { color: var(--bad, #b3261e); }`,
})
export class BudgetsPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Erp2Api);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly months = MONTH_NAMES;
  protected readonly shortMonths = MONTH_NAMES.map(m => m.slice(0, 3));
  protected readonly list = signal<BudgetSummary[]>([]);
  protected readonly branches = signal<Branch[]>([]);
  protected readonly open = signal<BudgetDetail | null>(null);
  protected readonly vari = signal<Variance | null>(null);
  protected readonly creating = signal(false);
  private readonly edits = signal<Record<string, number[]>>({});
  private readonly version = signal(0);
  protected n = { name: '', year: new Date().getFullYear() + (new Date().getMonth() >= 9 ? 1 : 0), branchId: null as number | null, start: 'last-year', growthPct: 10 };
  protected readonly totals = computed(() => { this.version(); const e = this.edits(); return Object.fromEntries(Object.entries(e).map(([k, v]) => [k, v.reduce((s, x) => s + (Number(x) || 0), 0)])); });

  ngOnInit() { void this.load(); void this.api.branches().then(b => this.branches.set(b)).catch(() => {}); }
  private async load() { try { this.list.set(await this.api.budgets()); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected branchName(id: number | null) { return id === null ? 'Whole business' : this.branches().find(b => b.id === id)?.name ?? 'Branch'; }

  protected async show(id: number) {
    try {
      const d = await this.api.budget(id);
      this.open.set(d); this.vari.set(null);
      this.edits.set(Object.fromEntries(d.rows.map(r => [r.code, [...r.months]])));
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected setCell(code: string, i: number, v: number) { this.edits.update(e => { e[code][i] = Number(v) || 0; return e; }); this.version.update(x => x + 1); }
  protected rowTotal(code: string) { return this.totals()[code] ?? 0; }
  protected spreadAll() {
    this.edits.update(e => { for (const k of Object.keys(e)) { const t = e[k].reduce((s, x) => s + (Number(x) || 0), 0); const each = Math.round(t / 12 * 100) / 100; e[k] = e[k].map((_, i) => i < 11 ? each : Math.round((t - each * 11) * 100) / 100); } return e; });
    this.version.update(x => x + 1);
    const d = this.open(); if (d) this.open.set({ ...d, rows: d.rows.map(r => ({ ...r, months: [...this.edits()[r.code]] })) });
  }
  protected async saveRows() {
    const d = this.open(); if (!d) return;
    try { await this.api.saveBudgetRows(d.summary.id, Object.entries(this.edits()).map(([code, months]) => ({ code, months: months.map(Number) }))); this.toasts.ok('Budget saved.'); await this.load(); await this.show(d.summary.id); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async approve(yes: boolean) {
    const d = this.open(); if (!d) return;
    try { await this.api.approveBudget(d.summary.id, yes); await this.load(); await this.show(d.summary.id); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async remove() {
    const d = this.open(); if (!d) return;
    if ((await this.confirm.ask({ title: `Delete ${d.summary.name}?`, message: 'The budget and its figures are removed.', confirmLabel: 'Delete', danger: true })) === null) return;
    try { await this.api.deleteBudget(d.summary.id); this.open.set(null); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async showVariance(id: number, toMonth = new Date().getMonth() + 1) {
    try { this.vari.set(await this.api.variance(id, toMonth)); this.open.set(null); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async create() {
    try { const { id } = await this.api.createBudget({ ...this.n, year: Number(this.n.year), growthPct: Number(this.n.growthPct) || 0 }); this.creating.set(false); await this.load(); await this.show(id); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
}
