import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { DayPipe, NairaPipe } from '../../shared/ui';
import { Account, BalanceSheet, ErpApi, Journal, LedgerLine, MONTH_NAMES, Period, ProfitAndLoss, TrialBalance, firstOfMonth, firstOfYear, today } from './erp-api';

type Tab = 'statements' | 'journals' | 'accounts' | 'close';
type Statement = 'pl' | 'bs' | 'tb';

/**
 * The books: profit and loss, balance sheet and trial balance straight from the general ledger; journals (manual ones prepared by one person and
 * approved by another); the chart of accounts with a ledger per account; month close and opening balances.
 */
@Component({
  selector: 'app-accounting',
  imports: [FormsModule, Modal, NairaPipe, DayPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Accounting</h1><span class="muted">General ledger · double entry · every sale, purchase, expense, payroll and stock change posts here</span></div>
      <div class="tabs" role="tablist">
        @for (t of tabs; track t.id) { <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === t.id" (click)="go(t.id)">{{ t.label }}</button> }
      </div>

      @switch (tab()) {
        @case ('statements') {
          <section class="card">
            <div class="toolbar">
              <div class="seg" role="group" aria-label="Statement">
                <button type="button" [class.on]="stmt() === 'pl'" (click)="stmt.set('pl'); loadStatement()">Profit &amp; loss</button>
                <button type="button" [class.on]="stmt() === 'bs'" (click)="stmt.set('bs'); loadStatement()">Balance sheet</button>
                <button type="button" [class.on]="stmt() === 'tb'" (click)="stmt.set('tb'); loadStatement()">Trial balance</button>
              </div>
              <div class="grow"></div>
              @if (stmt() === 'pl') {
                <label class="inline">From <input class="input" type="date" [(ngModel)]="from" (change)="loadStatement()" /></label>
                <label class="inline">To <input class="input" type="date" [(ngModel)]="to" (change)="loadStatement()" /></label>
              } @else {
                <label class="inline">As at <input class="input" type="date" [(ngModel)]="asOf" (change)="loadStatement()" /></label>
              }
              <button type="button" class="btn" (click)="print()">Print</button>
              <a class="btn" [href]="fileUrl('pdf')" target="_blank" rel="noopener">PDF</a>
              <a class="btn" [href]="fileUrl('xlsx')" download>Excel</a>
            </div>
            <p class="pack muted sm">Full set — profit &amp; loss {{ from | day }} to {{ to | day }}, balance sheet and trial balance at {{ to | day }}:
              <a [href]="fileUrl('pdf', 'all')" target="_blank" rel="noopener">PDF</a> · <a [href]="fileUrl('xlsx', 'all')" download>Excel</a></p>

            @if (stmt() === 'pl' && pl(); as p) {
              <div class="statement" id="statement">
                <h2>Profit and loss</h2><p class="muted">{{ p.from | day }} – {{ p.to | day }}</p>
                <h3>Income</h3>
                @for (l of p.income; track l.code) { <div class="row" (click)="drill(l.code)"><span>{{ l.name }} <span class="code">{{ l.code }}</span></span><span class="mono">{{ l.amount | naira }}</span></div> }
                <div class="row total"><span>Total income</span><span class="mono">{{ p.totalIncome | naira }}</span></div>
                <h3>Expenses</h3>
                @for (l of p.expenses; track l.code) { <div class="row" (click)="drill(l.code)"><span>{{ l.name }} <span class="code">{{ l.code }}</span></span><span class="mono">{{ l.amount | naira }}</span></div> }
                <div class="row total"><span>Total expenses</span><span class="mono">{{ p.totalExpenses | naira }}</span></div>
                <div class="row grand" [class.loss]="p.netProfit < 0"><span>{{ p.netProfit < 0 ? 'Net loss' : 'Net profit' }}</span><span class="mono">{{ p.netProfit | naira }}</span></div>
              </div>
            }
            @if (stmt() === 'bs' && bs(); as b) {
              <div class="statement" id="statement">
                <h2>Balance sheet</h2><p class="muted">As at {{ b.asOf | day }} · @if (b.balanced) { <span class="ok">Balanced</span> } @else { <span class="bad">Does not balance</span> }</p>
                <h3>Assets</h3>
                @for (l of b.assets; track l.code) { <div class="row" (click)="drill(l.code)"><span>{{ l.name }} <span class="code">{{ l.code }}</span></span><span class="mono">{{ l.amount | naira }}</span></div> }
                <div class="row total"><span>Total assets</span><span class="mono">{{ b.totalAssets | naira }}</span></div>
                <h3>Liabilities</h3>
                @for (l of b.liabilities; track l.code) { <div class="row" (click)="drill(l.code)"><span>{{ l.name }} <span class="code">{{ l.code }}</span></span><span class="mono">{{ l.amount | naira }}</span></div> }
                <div class="row total"><span>Total liabilities</span><span class="mono">{{ b.totalLiabilities | naira }}</span></div>
                <h3>Equity</h3>
                @for (l of b.equity; track l.code) { <div class="row" (click)="drill(l.code)"><span>{{ l.name }} <span class="code">{{ l.code }}</span></span><span class="mono">{{ l.amount | naira }}</span></div> }
                <div class="row"><span>Earnings to date</span><span class="mono">{{ b.earningsToDate | naira }}</span></div>
                <div class="row total"><span>Total equity</span><span class="mono">{{ b.totalEquity | naira }}</span></div>
                <div class="row grand"><span>Liabilities + equity</span><span class="mono">{{ b.totalLiabilities + b.totalEquity | naira }}</span></div>
              </div>
            }
            @if (stmt() === 'tb' && tb(); as t) {
              <div class="table-wrap" id="statement"><table class="table">
                <thead><tr><th>Code</th><th>Account</th><th>Type</th><th class="num">Debit</th><th class="num">Credit</th><th class="num">Balance</th></tr></thead>
                <tbody>@for (r of t.rows; track r.code) {
                  <tr class="click" (click)="drill(r.code)"><td class="mono">{{ r.code }}</td><td>{{ r.name }}</td><td class="muted">{{ r.type }}</td>
                    <td class="num mono">{{ r.debit | naira }}</td><td class="num mono">{{ r.credit | naira }}</td><td class="num mono strong">{{ r.balance | naira }}</td></tr>
                }</tbody>
                <tfoot><tr><th colspan="3">@if (t.balanced) { <span class="ok">Balanced</span> } @else { <span class="bad">Out of balance</span> }</th>
                  <th class="num mono">{{ t.totalDebit | naira }}</th><th class="num mono">{{ t.totalCredit | naira }}</th><th></th></tr></tfoot>
              </table></div>
            }
            <p class="muted sm pad">Click any account to see every entry behind it.</p>
          </section>
        }

        @case ('journals') {
          <section class="card">
            <div class="toolbar">
              <label class="inline">From <input class="input" type="date" [(ngModel)]="jFrom" (change)="loadJournals()" /></label>
              <label class="inline">To <input class="input" type="date" [(ngModel)]="jTo" (change)="loadJournals()" /></label>
              <select class="input" aria-label="Status" [(ngModel)]="jStatus" (change)="loadJournals()"><option value="">All</option><option value="Draft">Awaiting approval</option><option value="Posted">Posted</option><option value="Reversed">Reversed</option></select>
              <div class="grow"></div>
              @if (auth.can('gl.journal.create')) { <button type="button" class="btn btn-primary" (click)="newJournal()">New journal</button> }
            </div>
            <div class="journals">
              @for (j of journals(); track j.id) {
                <article class="journal" [class.draft]="j.status === 'Draft'" [class.rev]="j.status === 'Reversed'">
                  <header>
                    <span class="mono">#{{ j.id }}</span><span>{{ j.date | day }}</span><span class="strong">{{ j.memo }}</span>
                    <span class="muted sm">{{ j.source }} · {{ j.preparedBy }}{{ j.approvedBy ? ' → ' + j.approvedBy : '' }}</span>
                    <span class="grow"></span><span class="stamp" [class]="'stamp stamp-' + statusKind(j.status)">{{ j.status === 'Draft' ? 'Awaiting approval' : j.status }}</span>
                    @if (j.status === 'Draft' && auth.can('gl.journal.approve')) { <button type="button" class="btn btn-sm btn-primary" (click)="approveJournal(j)">Approve</button> }
                    @if (j.status === 'Posted' && j.source === 'Manual' && auth.can('gl.journal.approve')) { <button type="button" class="btn btn-sm" (click)="reverseJournal(j)">Reverse</button> }
                  </header>
                  <table class="table compact"><tbody>
                    @for (l of j.lines; track $index) { <tr><td class="mono">{{ l.code }}</td><td [class.indent]="l.credit > 0">{{ l.account }}{{ l.note ? ' — ' + l.note : '' }}</td><td class="num mono">{{ l.debit ? (l.debit | naira) : '' }}</td><td class="num mono">{{ l.credit ? (l.credit | naira) : '' }}</td></tr> }
                  </tbody></table>
                </article>
              }
              @if (!journals().length) { <div class="empty"><strong>No journals in this range</strong></div> }
            </div>
          </section>
        }

        @case ('accounts') {
          <section class="card">
            <div class="toolbar"><h2 class="h3">Chart of accounts</h2><div class="grow"></div>
              @if (auth.can('gl.period.close')) { <button type="button" class="btn" (click)="addingAccount.set(true)">Add account</button> }</div>
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Code</th><th>Account</th><th>Type</th><th>Kind</th></tr></thead>
              <tbody>@for (a of accounts(); track a.id) {
                <tr class="click" (click)="drill(a.code)"><td class="mono">{{ a.code }}</td><td>{{ a.name }}</td><td>{{ a.type }}</td><td class="muted sm">{{ a.isControl ? 'Control (posted by the system only)' : 'General' }}</td></tr>
              }</tbody>
            </table></div>
          </section>
        }

        @case ('close') {
          <section class="card pad">
            <h2 class="h3">Month close</h2>
            <p class="muted sm">Soft-closed months accept postings only from finance users allowed to close periods. Locked months accept nothing; corrections go in an open month.</p>
            <div class="table-wrap"><table class="table">
              <thead><tr><th>Month</th><th>Status</th><th>Closed by</th><th>Note</th><th></th></tr></thead>
              <tbody>@for (p of periodRows(); track p.year * 100 + p.month) {
                <tr><td class="strong">{{ months[p.month - 1] }} {{ p.year }}</td><td><span class="stamp" [class]="'stamp stamp-' + (p.status === 'Open' ? 'ok' : p.status === 'Locked' ? 'bad' : 'warn')">{{ p.status === 'SoftClosed' ? 'Soft-closed' : p.status }}</span></td>
                  <td class="muted sm">{{ p.closedBy || '' }}{{ p.closedAt ? ' · ' + (p.closedAt | day) : '' }}</td><td class="sm">{{ p.note || '' }}</td>
                  <td class="actions">@if (auth.can('gl.period.close')) {
                    @if (p.status !== 'Open') { <button type="button" class="btn btn-sm" (click)="setPeriod(p, 'Open')">Reopen</button> }
                    @if (p.status !== 'SoftClosed') { <button type="button" class="btn btn-sm" (click)="setPeriod(p, 'SoftClosed')">Soft-close</button> }
                    @if (p.status !== 'Locked') { <button type="button" class="btn btn-sm btn-danger" (click)="setPeriod(p, 'Locked')">Lock</button> }
                  }</td></tr>
              }</tbody>
            </table></div>

            <div class="two">
              <div class="box">
                <h3 class="h4">Stock check</h3>
                @if (recon(); as r) {
                  <dl class="kv"><dt>Inventory in the ledger</dt><dd class="mono">{{ r.ledgerInventory | naira }}</dd><dt>Stock on hand at cost</dt><dd class="mono">{{ r.stockAtCost | naira }}</dd>
                    <dt>Difference</dt><dd class="mono" [class.bad]="r.difference !== 0" [class.ok]="r.difference === 0">{{ r.difference | naira }}</dd></dl>
                  <p class="muted sm">{{ r.difference === 0 ? 'The books agree with the shelves.' : 'Investigate before closing the month: a stock change was made without going through the system, or opening stock was never posted.' }}</p>
                }
              </div>
              @if (auth.can('gl.period.close')) {
                <div class="box">
                  <h3 class="h4">Opening balances</h3>
                  <p class="muted sm">When you start using the ledger: cash and bank on the start date. Stock, customer and supplier balances come from their own records.</p>
                  <form class="form-grid" (ngSubmit)="opening()">
                    <div class="field"><label for="od">Date</label><input id="od" class="input" type="date" name="od" [(ngModel)]="openDate" /></div>
                    <div class="field"><label for="oc">Cash on hand (₦)</label><input id="oc" class="input num" type="number" name="oc" [(ngModel)]="openCash" /></div>
                    <div class="field"><label for="ob">Bank (₦)</label><input id="ob" class="input num" type="number" name="ob" [(ngModel)]="openBank" /></div>
                    <div class="span-2"><button type="submit" class="btn">Post opening balances</button></div>
                  </form>
                </div>
              }
            </div>
          </section>
        }
      }
    </div>

    <app-modal [open]="!!ledger()" [heading]="ledgerTitle()" [wide]="true" (closed)="ledger.set(null)">
      <div class="toolbar tight">
        <label class="inline">From <input class="input" type="date" [(ngModel)]="lFrom" (change)="reloadLedger()" /></label>
        <label class="inline">To <input class="input" type="date" [(ngModel)]="lTo" (change)="reloadLedger()" /></label>
      </div>
      <div class="table-wrap"><table class="table">
        <thead><tr><th>Date</th><th>Entry</th><th>Source</th><th class="num">Debit</th><th class="num">Credit</th><th class="num">Balance</th></tr></thead>
        <tbody>@for (l of ledger() ?? []; track $index) {
          <tr><td>{{ l.date | day }}</td><td>{{ l.memo }} <span class="muted sm">#{{ l.journalId }}</span></td><td class="muted sm">{{ l.source }}</td>
            <td class="num mono">{{ l.debit ? (l.debit | naira) : '' }}</td><td class="num mono">{{ l.credit ? (l.credit | naira) : '' }}</td><td class="num mono strong">{{ l.runningBalance | naira }}</td></tr>
        }</tbody>
      </table></div>
      @if (!(ledger() ?? []).length) { <p class="muted">No entries in this range.</p> }
    </app-modal>

    <app-modal [open]="drafting()" heading="New journal" [wide]="true" (closed)="drafting.set(false)">
      <form id="jf" (ngSubmit)="saveJournal()">
        <div class="form-grid">
          <div class="field"><label for="jd">Date</label><input id="jd" class="input" type="date" name="jd" [(ngModel)]="jDate" /></div>
          <div class="field"><label for="jm">Description</label><input id="jm" class="input" name="jm" [(ngModel)]="jMemo" placeholder="e.g. Accrue December audit fee" /></div>
        </div>
        <table class="table compact jl"><thead><tr><th>Account</th><th class="num">Debit</th><th class="num">Credit</th><th>Note</th><th></th></tr></thead>
          <tbody>@for (l of jLines(); track $index; let i = $index) {
            <tr><td><select class="input" [name]="'ja' + i" [(ngModel)]="l.code" aria-label="Account"><option value="">Choose…</option>@for (a of postable(); track a.code) { <option [value]="a.code">{{ a.code }} {{ a.name }}</option> }</select></td>
              <td><input class="input num" type="number" min="0" step="0.01" [name]="'jdr' + i" [(ngModel)]="l.debit" (ngModelChange)="bump()" aria-label="Debit" /></td>
              <td><input class="input num" type="number" min="0" step="0.01" [name]="'jcr' + i" [(ngModel)]="l.credit" (ngModelChange)="bump()" aria-label="Credit" /></td>
              <td><input class="input" [name]="'jn' + i" [(ngModel)]="l.note" aria-label="Note" /></td>
              <td><button type="button" class="btn btn-sm btn-quiet" (click)="removeLine(i)" aria-label="Remove">×</button></td></tr>
          }</tbody>
          <tfoot><tr><th><button type="button" class="btn btn-sm" (click)="addLine()">+ Line</button></th><th class="num mono">{{ jTotals().dr | naira }}</th><th class="num mono">{{ jTotals().cr | naira }}</th>
            <th colspan="2">@if (jTotals().dr !== jTotals().cr) { <span class="bad">Out by {{ jTotals().dr - jTotals().cr | naira }}</span> } @else if (jTotals().dr > 0) { <span class="ok">Balanced</span> }</th></tr></tfoot>
        </table>
        <p class="muted sm">A manual journal is posted only after someone else approves it. Control accounts (receivables, payables, inventory…) are posted by the system only.</p>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="drafting.set(false)">Cancel</button>
        <button type="submit" form="jf" class="btn btn-primary" [disabled]="busy() || jTotals().dr !== jTotals().cr || jTotals().dr <= 0">Submit for approval</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="addingAccount()" heading="Add an account" (closed)="addingAccount.set(false)">
      <form id="af" class="form-grid" (ngSubmit)="addAccount()">
        <div class="field"><label for="ac">Code</label><input id="ac" class="input mono" name="ac" [(ngModel)]="aCode" placeholder="e.g. 6150" /></div>
        <div class="field"><label for="at">Type</label><select id="at" class="input" name="at" [(ngModel)]="aType">@for (t of types; track t) { <option [value]="t">{{ t }}</option> }</select></div>
        <div class="field span-2"><label for="an">Name</label><input id="an" class="input" name="an" [(ngModel)]="aName" /></div>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="addingAccount.set(false)">Cancel</button>
        <button type="submit" form="af" class="btn btn-primary" [disabled]="!aCode || !aName">Add</button>
      </ng-container>
    </app-modal>`,
  styles: `
    .sm { font-size: .78rem; } .pad { padding: 1rem; } .h3 { margin: 0 0 .4rem; } .inline { display: flex; align-items: center; gap: .35rem; font-size: .85rem; }
    .seg { display: inline-flex; border: 1px solid var(--line-strong, var(--line)); border-radius: 10px; overflow: hidden; }
    .seg button { border: 0; background: none; padding: .45rem .9rem; font: inherit; cursor: pointer; } .seg button.on { background: var(--ink); color: #fff; }
    .statement { max-width: 46rem; padding: 0 1rem 1rem; } .statement h2 { margin: .5rem 0 0; } .statement h3 { margin: 1.1rem 0 .3rem; font-size: .8rem; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); }
    .row { display: flex; justify-content: space-between; padding: .35rem .25rem; border-bottom: 1px solid var(--line); cursor: pointer; } .row:hover { background: var(--sack); }
    .row.total { font-weight: 700; cursor: default; border-bottom: 2px solid var(--line-strong, var(--ink)); } .row.grand { font-weight: 800; font-size: 1.1rem; border-bottom: 3px double var(--ink); cursor: default; }
    .row.loss .mono { color: var(--bad, #b3261e); } .code { color: var(--muted); font-size: .72rem; font-family: var(--font-mono, monospace); }
    .ok { color: var(--ok, #0f7a6d); font-weight: 700; } .bad { color: var(--bad, #b3261e); font-weight: 700; } tr.click { cursor: pointer; }
    .journals { display: grid; gap: .8rem; padding: 0 1rem 1rem; } .journal { border: 1px solid var(--line); border-radius: 10px; overflow: hidden; }
    .journal header { display: flex; flex-wrap: wrap; gap: .6rem; align-items: center; padding: .5rem .8rem; background: var(--sack); }
    .journal.draft { border-color: var(--warn, #c77c02); } .journal.rev { opacity: .65; } .indent { padding-left: 2rem !important; }
    .compact td, .compact th { padding: .3rem .6rem; } .jl select { min-width: 14rem; } .jl .num { width: 9rem; }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; margin-top: 1.2rem; } @media (max-width: 800px) { .two { grid-template-columns: 1fr; } }
    .box { border: 1px solid var(--line); border-radius: 12px; padding: .9rem 1rem; } .kv { display: grid; grid-template-columns: 1fr auto; gap: .3rem 1rem; }
    .tight { padding: 0 0 .6rem; } .actions { white-space: nowrap; } .pack { padding: 0 1rem .4rem; margin: 0; } .pack a { font-weight: 600; }`,
})
export class AccountingPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(ErpApi);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly months = MONTH_NAMES;
  protected readonly types = ['Asset', 'Liability', 'Equity', 'Income', 'Expense'];
  protected readonly tabs: { id: Tab; label: string }[] = [
    { id: 'statements', label: 'Statements' }, { id: 'journals', label: 'Journals' }, { id: 'accounts', label: 'Chart of accounts' }, { id: 'close', label: 'Month close' },
  ];
  protected readonly tab = signal<Tab>('statements');
  protected readonly stmt = signal<Statement>('pl');
  protected readonly pl = signal<ProfitAndLoss | null>(null);
  protected readonly bs = signal<BalanceSheet | null>(null);
  protected readonly tb = signal<TrialBalance | null>(null);
  protected readonly journals = signal<Journal[]>([]);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly periods = signal<Period[]>([]);
  protected readonly recon = signal<{ ledgerInventory: number; stockAtCost: number; difference: number } | null>(null);
  protected readonly ledger = signal<LedgerLine[] | null>(null);
  protected readonly ledgerTitle = signal('');
  protected readonly drafting = signal(false);
  protected readonly addingAccount = signal(false);
  protected readonly busy = signal(false);
  protected readonly jLines = signal<{ code: string; debit: number; credit: number; note: string }[]>([]);
  private readonly edits = signal(0);
  protected readonly jTotals = computed(() => {
    this.edits();
    const r = (n: number) => Math.round(n * 100) / 100;
    return { dr: r(this.jLines().reduce((s, l) => s + (+l.debit || 0), 0)), cr: r(this.jLines().reduce((s, l) => s + (+l.credit || 0), 0)) };
  });
  protected readonly postable = computed(() => this.accounts().filter(a => a.isActive && !a.isControl));
  /** The last twelve months, newest first, with their close status (months never touched are Open). */
  protected readonly periodRows = computed(() => {
    const now = new Date(); const rows: Period[] = [];
    for (let i = 0; i < 12; i++) {
      const d = new Date(now.getFullYear(), now.getMonth() - i, 1);
      rows.push(this.periods().find(p => p.year === d.getFullYear() && p.month === d.getMonth() + 1)
        ?? { year: d.getFullYear(), month: d.getMonth() + 1, status: 'Open', closedAt: null, closedBy: null, note: null });
    }
    return rows;
  });

  protected from = firstOfYear(); protected to = today(); protected asOf = today();
  protected jFrom = firstOfMonth(); protected jTo = today(); protected jStatus = '';
  protected lFrom = firstOfYear(); protected lTo = today(); private lCode = '';
  protected jDate = today(); protected jMemo = '';
  protected aCode = ''; protected aName = ''; protected aType = 'Expense';
  protected openDate = firstOfYear(); protected openCash = 0; protected openBank = 0;

  ngOnInit() { void this.loadStatement(); void this.api.accounts().then(a => this.accounts.set(a)).catch(() => {}); }

  protected go(t: Tab) {
    this.tab.set(t);
    if (t === 'journals') void this.loadJournals();
    if (t === 'accounts') void this.loadAccounts();
    if (t === 'close') { void this.api.periods().then(p => this.periods.set(p)).catch(e => this.toasts.error(messageOf(e))); void this.api.inventoryReconciliation().then(r => this.recon.set(r)).catch(() => {}); }
  }

  protected async loadStatement() {
    try {
      if (this.stmt() === 'pl') this.pl.set(await this.api.profitAndLoss(this.from, this.to));
      if (this.stmt() === 'bs') this.bs.set(await this.api.balanceSheet(this.asOf));
      if (this.stmt() === 'tb') this.tb.set(await this.api.trialBalance(this.asOf));
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  /** The statement on screen (or the full set) as a file: the server renders it from the same ledger, on the business's letterhead. */
  protected fileUrl(format: 'pdf' | 'xlsx', which: Statement | 'all' = this.stmt()): string {
    const to = which === 'bs' || which === 'tb' ? this.asOf : this.to;
    return `/api/gl/statements?which=${which}&format=${format}&from=${this.from}&to=${to}`;
  }

  protected async loadJournals() { try { this.journals.set(await this.api.journals(this.jFrom, this.jTo, this.jStatus || undefined)); } catch (e) { this.toasts.error(messageOf(e)); } }
  private async loadAccounts() { try { this.accounts.set(await this.api.accounts()); } catch (e) { this.toasts.error(messageOf(e)); } }

  protected statusKind(s: string) { return s === 'Posted' ? 'ok' : s === 'Draft' ? 'warn' : 'bad'; }

  protected async drill(code: string) {
    this.lCode = code;
    const a = this.accounts().find(x => x.code === code);
    this.ledgerTitle.set(`${code} ${a?.name ?? ''}`);
    await this.reloadLedger();
  }
  protected async reloadLedger() { try { this.ledger.set(await this.api.ledger(this.lCode, this.lFrom, this.lTo)); } catch (e) { this.toasts.error(messageOf(e)); } }

  protected newJournal() { this.jLines.set([{ code: '', debit: 0, credit: 0, note: '' }, { code: '', debit: 0, credit: 0, note: '' }]); this.jMemo = ''; this.jDate = today(); this.drafting.set(true); }
  protected addLine() { this.jLines.update(l => [...l, { code: '', debit: 0, credit: 0, note: '' }]); }
  protected removeLine(i: number) { this.jLines.update(l => l.filter((_, n) => n !== i)); this.bump(); }
  protected bump() { this.edits.update(n => n + 1); }

  protected async saveJournal() {
    this.busy.set(true);
    try {
      const lines = this.jLines().filter(l => l.code && ((+l.debit || 0) > 0 || (+l.credit || 0) > 0)).map(l => ({ code: l.code, debit: +l.debit || 0, credit: +l.credit || 0, note: l.note.trim() || null }));
      const { id } = await this.api.draftJournal(this.jDate, this.jMemo.trim(), lines);
      this.toasts.ok(`Journal #${id} is waiting for approval.`); this.drafting.set(false); this.jStatus = ''; await this.loadJournals();
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async approveJournal(j: Journal) {
    if ((await this.confirm.ask({ title: `Approve journal #${j.id}?`, message: `${j.memo}. It is posted to the ledger on ${j.date}.`, confirmLabel: 'Approve and post' })) === null) return;
    try { await this.api.approveJournal(j.id); this.toasts.ok('Posted.'); await this.loadJournals(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async reverseJournal(j: Journal) {
    const reason = await this.confirm.ask({ title: `Reverse journal #${j.id}?`, message: 'An equal and opposite journal is posted today; the original stays on record.', confirmLabel: 'Reverse', danger: true,
      reason: { label: 'Why?', required: true } });
    if (reason === null) return;
    try { await this.api.reverseJournal(j.id, reason || 'Reversed'); this.toasts.ok('Reversed.'); await this.loadJournals(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async setPeriod(p: Period, status: string) {
    const label = status === 'Locked' ? 'Lock' : status === 'SoftClosed' ? 'Soft-close' : 'Reopen';
    const note = await this.confirm.ask({ title: `${label} ${MONTH_NAMES[p.month - 1]} ${p.year}?`, message: status === 'Locked' ? 'Nothing more can be posted into a locked month.' : 'You can change this later.',
      confirmLabel: label, danger: status === 'Locked', reason: { label: 'Note (optional)', required: false } });
    if (note === null) return;
    try { await this.api.setPeriod(p.year, p.month, status, note || null); this.periods.set(await this.api.periods()); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async opening() {
    try { const r = await this.api.openingBalances(this.openDate, Number(this.openCash) || 0, Number(this.openBank) || 0); this.toasts.ok(`Opening balances posted (journal #${r.journalId}).`); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async addAccount() {
    try { await this.api.addAccount(this.aCode.trim(), this.aName.trim(), this.aType); this.addingAccount.set(false); this.aCode = ''; this.aName = ''; await this.loadAccounts(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected print() { window.print(); }
}
