import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { DayPipe, NairaPipe, StampTimePipe } from '../../shared/ui';
import { Account, ErpApi, today } from './erp-api';
import { BankAccount, FinanceApi, LedgerItem, ReconcileView, StatementLine } from './finance-api';

/**
 * Bank reconciliation: import the statement, let the system match it to the books, deal with what's left (match by hand, post bank charges,
 * ignore), then sign off when statement + uncleared receipts − uncleared payments equals the books.
 */
@Component({
  selector: 'app-banking',
  imports: [FormsModule, Modal, NairaPipe, DayPipe, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Bank reconciliation</h1><span class="muted">Agree your bank statement with your books</span></div>

      <section class="card">
        <div class="toolbar">
          <div class="accts">
            @for (a of accounts(); track a.id) {
              <button type="button" class="acct" [class.on]="sel()?.id === a.id" (click)="select(a)">
                <span class="strong">{{ a.name }}</span><span class="muted sm">{{ a.bankName }} {{ a.accountNumber }} · ledger {{ a.glCode }}</span>
                <span class="mono">{{ a.ledgerBalance | naira }}</span>
                <span class="sm">{{ a.unmatched }} to match · {{ a.lastReconciled ? 'reconciled to ' + (a.lastReconciled | day) : 'never reconciled' }}</span>
              </button>
            }
          </div>
          <div class="grow"></div>
          <button type="button" class="btn" (click)="adding.set(true)">Add bank account</button>
        </div>
      </section>

      @if (view(); as v) {
        <section class="card pad">
          <div class="toolbar tight">
            <label class="btn btn-primary file">Import statement (CSV)<input type="file" accept=".csv,text/csv" (change)="importFile($event)" hidden /></label>
            <button type="button" class="btn" (click)="autoMatch()">Match automatically</button>
            <div class="grow"></div>
            <label class="inline">Statement date <input class="input" type="date" [(ngModel)]="asOf" (change)="load()" /></label>
            <label class="inline">Statement closing balance <input class="input num w" type="number" step="0.01" [(ngModel)]="stmtBalance" (change)="load()" /></label>
          </div>

          <div class="recon">
            <div><span>Statement balance</span><b>{{ v.statementBalance | naira }}</b></div>
            <div><span>+ Receipts not yet on statement</span><b>{{ v.unclearedReceipts | naira }}</b></div>
            <div><span>− Payments not yet on statement</span><b>{{ v.unclearedPayments | naira }}</b></div>
            <div><span>= Adjusted bank balance</span><b>{{ v.adjustedBankBalance | naira }}</b></div>
            <div><span>Balance in the books</span><b>{{ v.ledgerBalance | naira }}</b></div>
            <div [class.good]="v.difference === 0" [class.badbox]="v.difference !== 0"><span>Difference</span><b>{{ v.difference | naira }}</b></div>
          </div>
          <div class="acts">
            <button type="button" class="btn btn-primary" [disabled]="v.difference !== 0" (click)="complete()">Sign off reconciliation to {{ v.asOf | day }}</button>
            @if (v.difference !== 0) { <span class="muted sm">Match or post the remaining lines until the difference is ₦0.00.</span> }
          </div>
        </section>

        <div class="two">
          <section class="card">
            <h2 class="h4 pad">Bank statement</h2>
            <div class="table-wrap"><table class="table compact">
              <thead><tr><th>Date</th><th>Description</th><th class="num">Amount</th><th>Status</th><th></th></tr></thead>
              <tbody>@for (s of v.statement; track s.id) {
                <tr [class.pick]="picking()?.id === s.id">
                  <td>{{ s.date | day }}</td><td>{{ s.description }}<div class="muted sm">{{ s.reference }}{{ s.matchedTo ? ' → ' + s.matchedTo : '' }}</div></td>
                  <td class="num mono" [class.neg]="s.amount < 0">{{ s.amount | naira }}</td>
                  <td><span class="stamp" [class]="'stamp stamp-' + (s.status === 'Unmatched' ? 'warn' : s.status === 'Ignored' ? 'info' : 'ok')">{{ s.status }}</span></td>
                  <td class="actions">
                    @if (s.status === 'Unmatched') {
                      <button type="button" class="btn btn-sm" (click)="picking.set(s)">Match…</button>
                      <button type="button" class="btn btn-sm" (click)="startPost(s)">Post</button>
                      <button type="button" class="btn btn-sm btn-quiet" (click)="ignore(s)">Ignore</button>
                    } @else if (s.status === 'Matched') { <button type="button" class="btn btn-sm btn-quiet" (click)="unmatch(s)">Unmatch</button> }
                  </td>
                </tr>
              }</tbody>
            </table></div>
            @if (!v.statement.length) { <div class="empty"><strong>No statement imported</strong>Download your statement from internet banking as CSV (Excel works: Save as CSV) and import it.</div> }
          </section>

          <section class="card">
            <h2 class="h4 pad">In the books, not yet on the statement</h2>
            @if (picking(); as p) { <p class="notice pad">Pick the entry for <b>{{ p.description }}</b> ({{ p.amount | naira }}) — or <button type="button" class="link" (click)="picking.set(null)">cancel</button>.</p> }
            <div class="table-wrap"><table class="table compact">
              <thead><tr><th>Date</th><th>Entry</th><th class="num">Amount</th><th></th></tr></thead>
              <tbody>@for (u of candidates(); track u.glLineId) {
                <tr [class.fit]="picking() && u.amount === picking()!.amount"><td>{{ u.date | day }}</td><td>{{ u.memo }}<div class="muted sm">#{{ u.journalId }} · {{ u.source }}</div></td>
                  <td class="num mono" [class.neg]="u.amount < 0">{{ u.amount | naira }}</td>
                  <td>@if (picking()) { <button type="button" class="btn btn-sm btn-primary" (click)="match(u)">This one</button> }</td></tr>
              }</tbody>
            </table></div>
          </section>
        </div>

        @if (v.history.length) {
          <section class="card">
            <h2 class="h4 pad">Signed-off reconciliations</h2>
            <div class="table-wrap"><table class="table compact">
              <thead><tr><th>To</th><th class="num">Statement</th><th class="num">Books</th><th class="num">Uncleared in</th><th class="num">Uncleared out</th><th>By</th></tr></thead>
              <tbody>@for (h of v.history; track h.id) { <tr><td>{{ h.statementDate | day }}</td><td class="num mono">{{ h.statementBalance | naira }}</td><td class="num mono">{{ h.ledgerBalance | naira }}</td>
                <td class="num mono">{{ h.unclearedReceipts | naira }}</td><td class="num mono">{{ h.unclearedPayments | naira }}</td><td class="sm">{{ h.completedBy }} · {{ h.completedAt | stampTime }}</td></tr> }</tbody>
            </table></div>
          </section>
        }
      }
    </div>

    <app-modal [open]="!!posting()" heading="Post from the statement" (closed)="posting.set(null)">
      @if (posting(); as s) {
        <p>{{ s.date | day }} · {{ s.description }} · <b>{{ s.amount | naira }}</b></p>
        <form id="pf" class="form-grid" (ngSubmit)="post()">
          <div class="field span-2"><label for="pa">What is it?</label>
            <select id="pa" class="input" name="pa" [(ngModel)]="postCode"><option value="">Choose an account…</option>
              @for (a of postable(); track a.code) { <option [value]="a.code">{{ a.code }} {{ a.name }}</option> }</select></div>
          <div class="field span-2"><label for="pm">Description</label><input id="pm" class="input" name="pm" [(ngModel)]="postMemo" /></div>
        </form>
        <p class="muted sm">For things the books don't have yet: bank charges, SMS alerts, stamp duty, interest earned. A customer receipt or supplier payment should be recorded on its own screen and then matched.</p>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="posting.set(null)">Cancel</button>
        <button type="submit" form="pf" class="btn btn-primary" [disabled]="!postCode">Post and clear</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="adding()" heading="Add a bank account" (closed)="adding.set(false)">
      <form id="ba" class="form-grid" (ngSubmit)="addAccount()">
        <div class="field span-2"><label for="bn">Name</label><input id="bn" class="input" name="bn" [(ngModel)]="newName" placeholder="e.g. GTBank current" /></div>
        <div class="field"><label for="bb">Bank</label><input id="bb" class="input" name="bb" [(ngModel)]="newBank" /></div>
        <div class="field"><label for="bc">Account number</label><input id="bc" class="input mono" name="bc" [(ngModel)]="newNumber" /></div>
      </form>
      <p class="muted sm">It gets its own ledger account (1011, 1012…) so it reconciles separately. Move money between accounts with a journal.</p>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="adding.set(false)">Cancel</button>
        <button type="submit" form="ba" class="btn btn-primary" [disabled]="!newName">Add</button>
      </ng-container>
    </app-modal>`,
  styles: `.pad { padding: .9rem 1rem; } .sm { font-size: .78rem; } .inline { display: flex; gap: .35rem; align-items: center; font-size: .85rem; } .w { width: 10rem; }
    .accts { display: flex; flex-wrap: wrap; gap: .6rem; } .acct { display: grid; gap: .15rem; text-align: left; padding: .6rem .9rem; border: 1px solid var(--line); border-radius: 12px; background: none; cursor: pointer; font: inherit; color: inherit; }
    .acct.on { border-color: var(--ink); box-shadow: 0 0 0 2px var(--brand, #ffb400); } .file { cursor: pointer; } .tight { padding: 0 0 .8rem; display: flex; gap: .6rem; flex-wrap: wrap; align-items: center; }
    .recon { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: .5rem; } .recon div { display: grid; gap: .1rem; padding: .6rem .8rem; border: 1px solid var(--line); border-radius: 10px; }
    .recon span { font-size: .72rem; color: var(--muted); } .recon b { font-family: var(--font-mono, monospace); } .good { border-color: var(--ok, #0f7a6d) !important; } .badbox { border-color: var(--bad, #b3261e) !important; }
    .acts { display: flex; gap: .6rem; align-items: center; margin-top: .8rem; } .two { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; } @media (max-width: 1000px) { .two { grid-template-columns: 1fr; } }
    .compact td, .compact th { padding: .3rem .55rem; } .neg { color: var(--bad, #b3261e); } tr.pick td { background: var(--sack); } tr.fit td { background: color-mix(in srgb, var(--ok, #0f7a6d) 10%, transparent); }
    .actions { white-space: nowrap; } .link { border: 0; background: none; text-decoration: underline; cursor: pointer; font: inherit; color: inherit; }`,
})
export class BankingPage implements OnInit {
  private readonly api = inject(FinanceApi);
  private readonly erp = inject(ErpApi);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly accounts = signal<BankAccount[]>([]);
  protected readonly sel = signal<BankAccount | null>(null);
  protected readonly view = signal<ReconcileView | null>(null);
  protected readonly picking = signal<StatementLine | null>(null);
  protected readonly posting = signal<StatementLine | null>(null);
  protected readonly adding = signal(false);
  protected readonly glAccounts = signal<Account[]>([]);
  protected readonly postable = computed(() => this.glAccounts().filter(a => a.isActive && !a.isControl && !a.code.startsWith('101')));
  /** While matching, entries of the same amount first. */
  protected readonly candidates = computed(() => {
    const v = this.view(); if (!v) return [];
    const p = this.picking();
    return p ? [...v.uncleared].sort((a, b) => Number(b.amount === p.amount) - Number(a.amount === p.amount) || Math.abs(Date.parse(a.date) - Date.parse(p.date)) - Math.abs(Date.parse(b.date) - Date.parse(p.date))) : v.uncleared;
  });
  protected asOf = today(); protected stmtBalance: number | null = null;
  protected postCode = ''; protected postMemo = '';
  protected newName = ''; protected newBank = ''; protected newNumber = '';

  ngOnInit() {
    void this.loadAccounts();
    void this.erp.accounts().then(a => this.glAccounts.set(a)).catch(() => {});
  }

  private async loadAccounts() {
    try {
      this.accounts.set(await this.api.bankAccounts());
      const keep = this.accounts().find(a => a.id === this.sel()?.id) ?? this.accounts()[0];
      if (keep) { this.sel.set(keep); await this.load(); }
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async select(a: BankAccount) { this.sel.set(a); this.stmtBalance = null; this.picking.set(null); await this.load(); }

  protected async load() {
    const a = this.sel(); if (!a) return;
    try {
      const v = await this.api.reconcile(a.id, this.asOf, this.stmtBalance);
      this.view.set(v);
      if (this.stmtBalance === null) this.stmtBalance = v.statementBalance;
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async importFile(ev: Event) {
    const input = ev.target as HTMLInputElement; const file = input.files?.[0]; input.value = '';
    const a = this.sel(); if (!file || !a) return;
    try {
      const r = await this.api.importStatement(a.id, file);
      this.toasts.ok(`Imported ${r.imported} line(s), matched ${r.autoMatched} automatically${r.duplicates ? `, skipped ${r.duplicates} already imported` : ''}.`);
      if (r.problems.length) this.toasts.error(r.problems.slice(0, 3).join(' '));
      this.stmtBalance = null; await this.loadAccounts();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async autoMatch() {
    const a = this.sel(); if (!a) return;
    try { const r = await this.api.autoMatch(a.id); this.toasts.ok(`${r.matched} matched.`); await this.loadAccounts(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async match(u: LedgerItem) {
    const p = this.picking(); if (!p) return;
    try { await this.api.match(p.id, u.glLineId); this.picking.set(null); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async unmatch(s: StatementLine) { try { await this.api.unmatch(s.id); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async ignore(s: StatementLine) {
    if ((await this.confirm.ask({ title: 'Ignore this line?', message: `${s.description} (${s.amount}). It won't be matched or posted — use this for a duplicate line on the statement.`, confirmLabel: 'Ignore' })) === null) return;
    try { await this.api.ignore(s.id); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected startPost(s: StatementLine) {
    const d = s.description.toLowerCase();
    this.postCode = /charge|fee|commission|sms|stamp|maint|vat on/.test(d) ? '6400' : /interest/.test(d) && s.amount > 0 ? '4900' : '';
    this.postMemo = s.description; this.posting.set(s);
  }
  protected async post() {
    const s = this.posting(); if (!s) return;
    try { await this.api.postLine(s.id, this.postCode, this.postMemo.trim() || null); this.posting.set(null); await this.loadAccounts(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async complete() {
    const a = this.sel(), v = this.view(); if (!a || !v) return;
    if ((await this.confirm.ask({ title: 'Sign off this reconciliation?', message: `${a.name} agreed to the statement at ${v.asOf}. Lines up to that date are locked to it.`, confirmLabel: 'Sign off' })) === null) return;
    try { await this.api.complete(a.id, v.asOf, v.statementBalance); this.toasts.ok('Reconciliation signed off.'); await this.loadAccounts(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async addAccount() {
    try { await this.api.addBankAccount(this.newName.trim(), this.newBank.trim(), this.newNumber.trim()); this.adding.set(false); this.newName = this.newBank = this.newNumber = ''; await this.loadAccounts(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
}
