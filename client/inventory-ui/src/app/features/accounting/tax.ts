import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { DayPipe, NairaPipe } from '../../shared/ui';
import { MONTH_NAMES, today } from './erp-api';
import { FinanceApi, VatReturn } from './finance-api';

/** The monthly VAT return: what is owed, the schedule the tax office asks for, and filing and paying it. */
@Component({
  selector: 'app-tax',
  imports: [FormsModule, Modal, NairaPipe, DayPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Tax</h1><span class="muted">The monthly VAT return, straight from the books</span></div>
        <div class="toolbar card pad">
          <label class="inline">Month <select class="input" [ngModel]="month()" (ngModelChange)="month.set(+$event); load()">@for (m of months; track $index) { <option [ngValue]="$index + 1">{{ m }}</option> }</select></label>
          <input class="input year" type="number" aria-label="Year" [ngModel]="year()" (ngModelChange)="year.set(+$event); load()" />
        </div>

      @if (vat(); as v) {
        <section class="card pad">
          <div class="kpis">
            <div><span>Output VAT (on sales)</span><b>{{ v.outputVat | naira }}</b></div>
            <div><span>Input VAT (recoverable)</span><b>{{ v.inputVat | naira }}</b></div>
            <div><span>Credit brought forward</span><b>{{ v.creditBroughtForward | naira }}</b></div>
            <div class="hi"><span>VAT payable</span><b>{{ v.payable | naira }}</b></div>
          </div>
          <p class="muted sm">Due by {{ v.dueDate | day }} to the Nigeria Revenue Service.@if (v.filed) { <span class="ok"> Filed — paid {{ v.amountPaid | naira }}{{ v.reference ? ', ref ' + v.reference : '' }}.</span> }</p>
          <div class="acts">
            <button type="button" class="btn" (click)="csv()">Download return (CSV)</button>
            @if (!v.filed && (v.outputVat > 0 || v.inputVat > 0)) { <button type="button" class="btn btn-primary" (click)="filing.set(true)">File and pay</button> }
          </div>
          @for (sec of sections(v); track sec.title) {
            @if (sec.lines.length) {
              <h3 class="h4">{{ sec.title }}</h3>
              <div class="table-wrap"><table class="table compact">
                <thead><tr><th>Date</th><th>Reference</th><th>Party</th><th class="num">Net</th><th class="num">VAT</th></tr></thead>
                <tbody>@for (l of sec.lines; track $index) { <tr><td>{{ l.date | day }}</td><td class="mono">{{ l.reference }}</td><td>{{ l.party }}</td><td class="num mono">{{ l.net | naira }}</td><td class="num mono">{{ l.vat | naira }}</td></tr> }</tbody>
              </table></div>
            }
          }
        </section>
      }

    </div>

    <app-modal [open]="filing()" heading="File and pay VAT" (closed)="filing.set(false)">
      @if (vat(); as v) {
        <p>{{ months[v.month - 1] }} {{ v.year }}: output {{ v.outputVat | naira }} less input {{ v.inputVat | naira }}{{ v.creditBroughtForward ? ' and credit ' : '' }}{{ v.creditBroughtForward ? (v.creditBroughtForward | naira) : '' }}.</p>
        <p class="strong">Pay {{ v.payable | naira }} to the Nigeria Revenue Service.</p>
        <form id="vf" class="form-grid" (ngSubmit)="file()">
          <div class="field"><label for="vd">Paid on</label><input id="vd" class="input" type="date" name="vd" [(ngModel)]="paidOn" /></div>
          <div class="field"><label for="vr">Payment / filing reference</label><input id="vr" class="input" name="vr" [(ngModel)]="reference" /></div>
        </form>
        <p class="muted sm">Posts the settlement: output VAT cleared against input VAT, the rest paid from the bank. Unused input VAT carries forward.</p>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="filing.set(false)">Cancel</button>
        <button type="submit" form="vf" class="btn btn-primary">File and post</button>
      </ng-container>
    </app-modal>`,
  styles: `.pad { padding: 1rem; } .sm { font-size: .78rem; } .inline { display: flex; gap: .4rem; align-items: center; } .year { width: 6rem; } .toolbar.card { display: flex; gap: .6rem; margin-bottom: 1rem; }
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: .6rem; margin-bottom: .8rem; }
    .kpis div { display: grid; gap: .1rem; padding: .7rem .9rem; border: 1px solid var(--line); border-radius: 10px; } .kpis span { font-size: .75rem; color: var(--muted); } .kpis b { font-family: var(--font-mono, monospace); font-size: 1.1rem; }
    .kpis .hi { border-color: var(--ink); } .ok { color: var(--ok, #0f7a6d); font-weight: 600; } .acts { display: flex; gap: .5rem; margin: .6rem 0 1rem; } .compact td, .compact th { padding: .3rem .6rem; }
    .w { width: 6rem; } .actions { white-space: nowrap; } .cert dl { display: grid; grid-template-columns: 14rem 1fr; gap: .4rem 1rem; } .cert dt { color: var(--muted); }`,
})
export class TaxPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(FinanceApi);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly months = MONTH_NAMES;
  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(Math.max(1, new Date().getMonth()));
  protected readonly vat = signal<VatReturn | null>(null);
  protected readonly filing = signal(false);
  protected paidOn = today(); protected reference = '';

  ngOnInit() { void this.load(); }

  protected async load() {
    try {
      this.vat.set(await this.api.vat(this.year(), this.month()));
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected sections(v: VatReturn) { return [{ title: 'Sales (output VAT)', lines: v.sales }]; }

  protected async file() {
    try { await this.api.fileVat(this.year(), this.month(), this.paidOn || null, this.reference.trim() || null); this.filing.set(false); this.toasts.ok('VAT return filed and posted.'); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected csv() { void this.api.download(`api/tax/vat/${this.year()}/${this.month()}/return.csv`, `vat-${this.year()}-${String(this.month()).padStart(2, '0')}.csv`).catch(e => this.toasts.error(messageOf(e))); }
}
