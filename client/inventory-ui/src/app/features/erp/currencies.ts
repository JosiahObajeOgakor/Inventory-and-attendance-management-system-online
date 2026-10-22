import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { Api, messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { DayPipe, NairaPipe } from '../../shared/ui';
import { Account, ErpApi, today } from '../accounting/erp-api';
import { CurrencyRow, Erp2Api, FxDoc, FxRate, Revaluation } from './erp2-api';

/** Foreign currencies: rates, foreign bills and invoices, settlement with exchange gains/losses, and month-end revaluation. */
@Component({
  selector: 'app-currencies',
  imports: [FormsModule, Modal, NairaPipe, DayPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Currencies</h1><span class="muted">Naira is the base: foreign amounts are translated at the day's rate</span></div>

      <section class="card">
        <div class="toolbar"><h2 class="h4">Currencies and rates</h2><div class="grow"></div>
          @if (auth.can('gl.journal.create')) { <input class="input code" maxlength="3" placeholder="USD" [(ngModel)]="newCode" aria-label="Currency code" /><button type="button" class="btn" (click)="addCurrency()">Add currency</button> }</div>
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Currency</th><th class="num">Latest rate (₦)</th><th>Since</th><th></th></tr></thead>
          <tbody>@for (c of currencies(); track c.code) {
            <tr><td class="strong">{{ c.code }} <span class="muted">{{ c.name }}</span></td><td class="num mono">{{ c.latestRate ?? '—' }}</td><td>{{ c.latestDate | day }}</td>
              <td class="actions"><button type="button" class="btn btn-sm" (click)="openRates(c)">Rates</button></td></tr>
          }</tbody>
        </table></div>
      </section>

      <section class="card">
        <div class="toolbar"><h2 class="h4">Foreign bills and invoices</h2><div class="grow"></div>
          <label class="inline"><input type="checkbox" [(ngModel)]="openOnly" (change)="loadDocs()" /> Open only</label>
          @if (auth.can('gl.journal.create') && currencies().length) { <button type="button" class="btn btn-primary" (click)="openBook()">Book a bill or invoice</button> }</div>
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Number</th><th>Date</th><th>Party</th><th>Description</th><th class="num">Amount</th><th class="num">Rate</th><th class="num">₦</th><th class="num">Outstanding</th><th></th></tr></thead>
          <tbody>@for (d of docs(); track d.id) {
            <tr><td class="mono">{{ d.number }}<div class="muted sm">{{ d.kind === 'SupplierBill' ? 'Bill' : 'Invoice' }}</div></td><td>{{ d.date | day }}</td><td>{{ d.party }}</td><td>{{ d.description }}</td>
              <td class="num mono">{{ d.currency }} {{ d.foreignAmount.toLocaleString() }}</td><td class="num mono">{{ d.rate }}</td><td class="num mono">{{ d.baseAmount | naira }}</td>
              <td class="num mono strong">{{ d.foreignOutstanding ? d.currency + ' ' + d.foreignOutstanding.toLocaleString() : 'Settled' }}
                @for (s of d.settlements; track $index) { <div class="muted sm">{{ s.date | day }}: {{ s.foreignAmount.toLocaleString() }} @ {{ s.rate }} → {{ s.gainLoss >= 0 ? 'gain' : 'loss' }} {{ s.gainLoss | naira }}</div> }</td>
              <td>@if (!d.settled && auth.can('gl.journal.create')) { <button type="button" class="btn btn-sm" (click)="openSettle(d)">{{ d.kind === 'SupplierBill' ? 'Pay' : 'Receive' }}</button> }</td></tr>
          }</tbody>
        </table></div>
      </section>

      <section class="card pad">
        <div class="toolbar tight"><h2 class="h4">Month-end revaluation</h2><div class="grow"></div>
          <label class="inline">As at <input class="input" type="date" [(ngModel)]="revalDate" (change)="loadReval()" /></label></div>
        @if (reval(); as r) {
          @if (r.rows.length) {
            <div class="table-wrap"><table class="table compact">
              <thead><tr><th>Document</th><th>Party</th><th class="num">Outstanding</th><th class="num">Book rate</th><th class="num">Closing rate</th><th class="num">Unrealised</th></tr></thead>
              <tbody>@for (x of r.rows; track x.number) { <tr><td class="mono">{{ x.number }}</td><td>{{ x.party }}</td><td class="num mono">{{ x.currency }} {{ x.foreignOutstanding.toLocaleString() }}</td>
                <td class="num mono">{{ x.bookRate }}</td><td class="num mono">{{ x.closingRate }}</td><td class="num mono" [class.good]="x.unrealised >= 0" [class.bad]="x.unrealised < 0">{{ x.unrealised | naira }}</td></tr> }</tbody>
            </table></div>
            <p class="strong">Unrealised {{ r.totalUnrealised >= 0 ? 'gain' : 'loss' }}: {{ r.totalUnrealised | naira }}</p>
            @if (r.posted) { <p class="good">Posted (and reversed the next day).</p> }
            @else if (auth.can('gl.journal.approve')) { <button type="button" class="btn" (click)="postReval()">Post revaluation</button> }
          } @else { <p class="muted">No open foreign balances on that date.</p> }
        }
      </section>
    </div>

    <app-modal [open]="!!ratesFor()" [heading]="'Rates — ' + (ratesFor()?.code ?? '')" (closed)="ratesFor.set(null)">
      @if (auth.can('gl.journal.create')) {
        <form class="form-grid" (ngSubmit)="saveRate()">
          <div class="field"><label for="rd">Date</label><input id="rd" class="input" type="date" name="rd" [(ngModel)]="rateDate" /></div>
          <div class="field"><label for="rv">₦ per 1 {{ ratesFor()?.code }}</label><input id="rv" class="input num" type="number" step="0.0001" name="rv" [(ngModel)]="rateValue" /></div>
          <div class="field span-2"><label for="rs">Source</label><input id="rs" class="input" name="rs" [(ngModel)]="rateSource" placeholder="e.g. CBN NFEM closing" /></div>
          <div class="span-2"><button type="submit" class="btn btn-primary" [disabled]="!(rateValue > 0)">Save rate</button></div>
        </form>
      }
      <ul class="lines">@for (r of rates(); track r.id) { <li><span>{{ r.date | day }}</span><span class="mono">{{ r.rate }}</span><span class="muted sm">{{ r.source }}</span></li> }</ul>
    </app-modal>

    <app-modal [open]="booking()" heading="Book a foreign bill or invoice" [wide]="true" (closed)="booking.set(false)">
      <form id="fb" class="form-grid" (ngSubmit)="book()">
        <div class="field"><label for="fk">Type</label><select id="fk" class="input" name="fk" [(ngModel)]="b.kind" (ngModelChange)="partyList()"><option value="SupplierBill">Bill from a supplier</option><option value="CustomerInvoice">Invoice to a customer</option></select></div>
        <div class="field"><label for="fp">{{ b.kind === 'SupplierBill' ? 'Supplier' : 'Customer' }}</label><select id="fp" class="input" name="fp" [(ngModel)]="b.partyId">@for (p of parties(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }</select></div>
        <div class="field"><label for="fc">Currency</label><select id="fc" class="input" name="fc" [(ngModel)]="b.currency">@for (c of currencies(); track c.code) { <option>{{ c.code }}</option> }</select></div>
        <div class="field"><label for="fa">Amount</label><input id="fa" class="input num" type="number" name="fa" [(ngModel)]="b.foreignAmount" /></div>
        <div class="field"><label for="fd">Date</label><input id="fd" class="input" type="date" name="fd" [(ngModel)]="b.date" /></div>
        <div class="field"><label for="fr">Rate (blank = rate on file)</label><input id="fr" class="input num" type="number" step="0.0001" name="fr" [(ngModel)]="b.rate" /></div>
        <div class="field span-2"><label for="fx">Description</label><input id="fx" class="input" name="fx" [(ngModel)]="b.description" /></div>
        <div class="field span-2"><label for="fo">{{ b.kind === 'SupplierBill' ? 'What was bought (account)' : 'Income account' }}</label>
          <select id="fo" class="input" name="fo" [(ngModel)]="b.accountCode">@for (a of accountsFor(); track a.code) { <option [value]="a.code">{{ a.code }} {{ a.name }}</option> }</select></div>
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="booking.set(false)">Cancel</button>
        <button type="submit" form="fb" class="btn btn-primary" [disabled]="!b.partyId || !(b.foreignAmount > 0) || !b.description || !b.accountCode">Book and post</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="!!settling()" [heading]="settling()?.kind === 'SupplierBill' ? 'Pay a foreign bill' : 'Receive on a foreign invoice'" (closed)="settling.set(null)">
      @if (settling(); as d) {
        <p>{{ d.number }} · outstanding {{ d.currency }} {{ d.foreignOutstanding.toLocaleString() }} (booked at {{ d.rate }})</p>
        <form id="sf" class="form-grid" (ngSubmit)="settle()">
          <div class="field"><label for="sa">Amount ({{ d.currency }})</label><input id="sa" class="input num" type="number" name="sa" [(ngModel)]="s.foreignAmount" /></div>
          <div class="field"><label for="sd">Date</label><input id="sd" class="input" type="date" name="sd" [(ngModel)]="s.date" /></div>
          <div class="field"><label for="sr">Rate (blank = rate on file)</label><input id="sr" class="input num" type="number" step="0.0001" name="sr" [(ngModel)]="s.rate" /></div>
          <div class="field"><label for="sb">Bank account (ledger code)</label><input id="sb" class="input mono" name="sb" [(ngModel)]="s.bankCode" placeholder="1010" /></div>
        </form>
        <p class="muted sm">The difference between the booking rate and this rate is posted as a realised exchange gain or loss.</p>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="settling.set(null)">Cancel</button>
        <button type="submit" form="sf" class="btn btn-primary" [disabled]="!(s.foreignAmount > 0)">Post</button>
      </ng-container>
    </app-modal>`,
  styles: `.pad { padding: 1rem; } .sm { font-size: .75rem; } .inline { display: flex; gap: .35rem; align-items: center; font-size: .85rem; } .code { width: 5rem; text-transform: uppercase; }
    .actions { white-space: nowrap; } .tight { padding: 0 0 .6rem; display: flex; align-items: center; } .compact td, .compact th { padding: .3rem .6rem; }
    .lines { display: grid; gap: .25rem; margin-top: .8rem; max-height: 40vh; overflow: auto; } .lines li { display: grid; grid-template-columns: 8rem 8rem 1fr; }
    .good { color: var(--ok, #0f7a6d); } .bad { color: var(--bad, #b3261e); }`,
})
export class CurrenciesPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Erp2Api);
  private readonly erp = inject(ErpApi);
  private readonly core = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly currencies = signal<CurrencyRow[]>([]);
  protected readonly docs = signal<FxDoc[]>([]);
  protected readonly reval = signal<Revaluation | null>(null);
  protected readonly ratesFor = signal<CurrencyRow | null>(null);
  protected readonly rates = signal<FxRate[]>([]);
  protected readonly booking = signal(false);
  protected readonly settling = signal<FxDoc | null>(null);
  protected readonly parties = signal<{ id: number; name: string }[]>([]);
  private readonly accounts = signal<Account[]>([]);
  protected openOnly = true; protected newCode = ''; protected revalDate = today();
  protected rateDate = today(); protected rateValue = 0; protected rateSource = '';
  protected b = { kind: 'SupplierBill', partyId: 0, currency: '', foreignAmount: 0, date: today(), rate: null as number | null, description: '', accountCode: '1220' };
  protected s = { foreignAmount: 0, date: today(), rate: null as number | null, bankCode: '1010' };

  ngOnInit() { void this.load(); void this.loadDocs(); void this.loadReval(); void this.erp.accounts().then(a => this.accounts.set(a)).catch(() => {}); }
  private async load() { try { this.currencies.set(await this.api.currencies()); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async loadDocs() { try { this.docs.set(await this.api.fxDocuments(this.openOnly)); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async loadReval() { try { this.reval.set(await this.api.revaluation(this.revalDate)); } catch { this.reval.set(null); } }
  protected accountsFor() {
    const a = this.accounts().filter(x => x.isActive && (!x.isControl || x.code === '1220'));
    return this.b.kind === 'SupplierBill' ? a.filter(x => x.type === 'Expense' || x.type === 'Asset') : a.filter(x => x.type === 'Income');
  }

  protected async addCurrency() { try { await this.api.addCurrency(this.newCode.trim().toUpperCase()); this.newCode = ''; await this.load(); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async openRates(c: CurrencyRow) { this.ratesFor.set(c); this.rateValue = c.latestRate ?? 0; try { this.rates.set(await this.api.rates(c.code)); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async saveRate() {
    const c = this.ratesFor(); if (!c) return;
    try { await this.api.setRate(c.code, this.rateDate, Number(this.rateValue), this.rateSource || null); this.rates.set(await this.api.rates(c.code)); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async partyList() {
    try {
      const items = this.b.kind === 'SupplierBill' ? (await this.core.suppliers({ page: 1, pageSize: 200 })).items : (await this.core.customers({ page: 1, pageSize: 200 })).items;
      this.parties.set(items.map(x => ({ id: x.id, name: x.name }))); this.b.partyId = this.parties()[0]?.id ?? 0;
      this.b.accountCode = this.b.kind === 'SupplierBill' ? '1220' : '4000';
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected openBook() { this.b = { ...this.b, currency: this.currencies()[0]?.code ?? '', foreignAmount: 0, description: '', rate: null, date: today() }; void this.partyList(); this.booking.set(true); }
  protected async book() {
    try {
      await this.api.bookFx({ kind: this.b.kind, partyId: this.b.partyId, currency: this.b.currency, date: this.b.date, dueDate: null, description: this.b.description,
        accountCode: this.b.accountCode, foreignAmount: Number(this.b.foreignAmount), rate: this.b.rate ? Number(this.b.rate) : null });
      this.booking.set(false); await this.loadDocs(); await this.loadReval();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected openSettle(d: FxDoc) { this.s = { foreignAmount: d.foreignOutstanding, date: today(), rate: null, bankCode: '1010' }; this.settling.set(d); }
  protected async settle() {
    const d = this.settling(); if (!d) return;
    try { await this.api.settleFx(d.id, { date: this.s.date, foreignAmount: Number(this.s.foreignAmount), rate: this.s.rate ? Number(this.s.rate) : null, bankCode: this.s.bankCode || null });
      this.settling.set(null); await this.loadDocs(); await this.loadReval(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async postReval() {
    if ((await this.confirm.ask({ title: 'Post the revaluation?', message: 'The unrealised gain or loss is posted at this date and reversed the next day.', confirmLabel: 'Post' })) === null) return;
    try { await this.api.postRevaluation(this.revalDate); await this.loadReval(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
