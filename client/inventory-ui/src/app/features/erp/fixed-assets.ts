import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { Api, messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { DayPipe, NairaPipe, StampTimePipe } from '../../shared/ui';
import { MONTH_NAMES, today } from '../accounting/erp-api';
import { Asset, AssetRegister, DepPreview, Erp2Api } from './erp2-api';

/** The fixed-asset register, monthly depreciation and disposals. */
@Component({
  selector: 'app-fixed-assets',
  imports: [FormsModule, Modal, NairaPipe, DayPipe, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Fixed assets</h1><div class="grow"></div>
        @if (auth.can('gl.journal.create')) {
          <button type="button" class="btn" (click)="openRun()">Run depreciation</button>
          <button type="button" class="btn btn-primary" (click)="openAdd()">Add asset</button>
        }
      </div>
      @if (reg(); as r) {
        <div class="kpis">
          <div><span>Cost</span><b>{{ r.totalCost | naira }}</b></div>
          <div><span>Accumulated depreciation</span><b>{{ r.totalAccumulated | naira }}</b></div>
          <div><span>Book value</span><b>{{ r.totalBookValue | naira }}</b></div>
          @for (c of r.byCategory; track c.category) { <div><span>{{ c.category }} ({{ c.count }})</span><b>{{ c.bookValue | naira }}</b></div> }
        </div>
        <section class="card">
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Asset</th><th>Category</th><th>Bought</th><th class="num">Cost</th><th class="num">Depreciation</th><th class="num">Book value</th><th class="num">Per month</th><th>Status</th><th></th></tr></thead>
            <tbody>@for (a of r.assets; track a.id) {
              <tr [class.gone]="a.status === 'Disposed'">
                <td><span class="strong">{{ a.name }}</span><div class="muted sm">{{ a.code }}{{ a.serialNumber ? ' · ' + a.serialNumber : '' }}{{ a.location ? ' · ' + a.location : '' }}</div></td>
                <td>{{ a.category }}<div class="muted sm">{{ method(a) }}</div></td><td>{{ a.acquiredOn | day }}</td>
                <td class="num mono">{{ a.cost | naira }}</td><td class="num mono">{{ a.accumulatedDepreciation | naira }}</td><td class="num mono strong">{{ a.bookValue | naira }}</td>
                <td class="num mono">{{ a.monthlyCharge | naira }}</td>
                <td><span class="stamp" [class]="'stamp stamp-' + (a.status === 'Active' ? 'ok' : a.status === 'Disposed' ? 'bad' : 'info')">{{ a.status === 'FullyDepreciated' ? 'Fully depreciated' : a.status }}</span>
                  @if (a.status === 'Disposed') { <div class="muted sm">{{ a.disposedOn | day }} · {{ (a.disposalGainLoss ?? 0) >= 0 ? 'gain' : 'loss' }} {{ (a.disposalGainLoss ?? 0) | naira }}</div> }</td>
                <td>@if (a.status !== 'Disposed' && auth.can('gl.journal.approve')) { <button type="button" class="btn btn-sm" (click)="openDispose(a)">Dispose</button> }</td>
              </tr>
            }</tbody>
          </table></div>
          @if (!r.assets.length) { <div class="empty"><strong>No assets yet</strong>Add vehicles, machinery, computers, furniture and buildings to depreciate them automatically.</div> }
        </section>
        @if (r.runs.length) {
          <section class="card"><h2 class="h4 pad">Depreciation runs</h2>
            <div class="table-wrap"><table class="table compact"><thead><tr><th>Month</th><th class="num">Assets</th><th class="num">Charged</th><th>Run</th></tr></thead>
              <tbody>@for (x of r.runs; track x.id) { <tr><td>{{ months[x.month - 1] }} {{ x.year }}</td><td class="num">{{ x.assets }}</td><td class="num mono">{{ x.total | naira }}</td><td class="sm">{{ x.runAt | stampTime }}</td></tr> }</tbody></table></div>
          </section>
        }
      }
    </div>

    <app-modal [open]="adding()" heading="Add a fixed asset" [wide]="true" (closed)="adding.set(false)">
      <form id="af" class="form-grid" (ngSubmit)="add()">
        <div class="field span-2"><label for="an">Name</label><input id="an" class="input" name="an" [(ngModel)]="a.name" placeholder="e.g. Toyota Hilux delivery van" /></div>
        <div class="field"><label for="ac">Category</label><select id="ac" class="input" name="ac" [(ngModel)]="a.category">@for (c of reg()?.categories ?? []; track c) { <option>{{ c }}</option> }</select></div>
        <div class="field"><label for="as">Serial / plate number</label><input id="as" class="input" name="as" [(ngModel)]="a.serialNumber" /></div>
        <div class="field"><label for="ad">Date bought</label><input id="ad" class="input" type="date" name="ad" [(ngModel)]="a.acquiredOn" /></div>
        <div class="field"><label for="ak">Cost (₦)</label><input id="ak" class="input num" type="number" name="ak" [(ngModel)]="a.cost" /></div>
        <div class="field"><label for="ar">Residual value (₦)</label><input id="ar" class="input num" type="number" name="ar" [(ngModel)]="a.residualValue" /></div>
        <div class="field"><label for="am">Method</label><select id="am" class="input" name="am" [(ngModel)]="a.method"><option value="StraightLine">Straight line</option><option value="ReducingBalance">Reducing balance</option></select></div>
        @if (a.method === 'StraightLine') { <div class="field"><label for="al">Useful life (months)</label><input id="al" class="input num" type="number" name="al" [(ngModel)]="a.usefulLifeMonths" /></div> }
        @else { <div class="field"><label for="ap">Rate per year (%)</label><input id="ap" class="input num" type="number" name="ap" [(ngModel)]="a.ratePct" /></div> }
        <div class="field"><label for="ay">Paid by</label><select id="ay" class="input" name="ay" [(ngModel)]="a.paidFrom"><option>Bank</option><option>Cash</option><option value="Credit">On credit from a supplier</option><option value="Opening">Already owned (bringing it into the books)</option></select></div>
        @if (a.paidFrom === 'Credit') { <div class="field"><label for="au">Supplier</label><select id="au" class="input" name="au" [(ngModel)]="a.supplierId">@for (s of suppliers(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }</select></div> }
        @if (a.paidFrom === 'Opening') { <div class="field"><label for="ao">Depreciation already charged (₦)</label><input id="ao" class="input num" type="number" name="ao" [(ngModel)]="a.openingAccumulated" /></div> }
        <div class="field span-2"><label for="aq">Location</label><input id="aq" class="input" name="aq" [(ngModel)]="a.location" /></div>
      </form>
      <p class="muted sm">Depreciation starts the month after purchase. Land isn't depreciated. Typical lives: computers 36, vehicles 48, furniture 60, plant 120 months.</p>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="adding.set(false)">Cancel</button>
        <button type="submit" form="af" class="btn btn-primary" [disabled]="!a.name || !(a.cost > 0)">Add and post</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="running()" heading="Run depreciation" (closed)="running.set(false)">
      <div class="form-grid">
        <div class="field"><label for="rm">Month</label><select id="rm" class="input" [(ngModel)]="runMonth" (ngModelChange)="preview()">@for (m of months; track $index) { <option [ngValue]="$index + 1">{{ m }}</option> }</select></div>
        <div class="field"><label for="ry">Year</label><input id="ry" class="input" type="number" [(ngModel)]="runYear" (change)="preview()" /></div>
      </div>
      @if (prev(); as p) {
        @if (p.alreadyRun && !p.lines.length) { <p class="ok">Already run for this month.</p> }
        @else {
          <ul class="lines">@for (l of p.lines; track l.assetId) { <li><span>{{ l.code }} {{ l.name }}</span><span class="mono">{{ l.amount | naira }}</span></li> }</ul>
          <p class="strong">Total {{ p.total | naira }}</p>
        }
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="running.set(false)">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="!prev()?.lines?.length" (click)="run()">Post depreciation</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="!!disposing()" heading="Dispose of an asset" (closed)="disposing.set(null)">
      @if (disposing(); as d) {
        <p>{{ d.code }} {{ d.name }} · book value {{ d.bookValue | naira }}</p>
        <form id="df" class="form-grid" (ngSubmit)="dispose()">
          <div class="field"><label for="dd">Date</label><input id="dd" class="input" type="date" name="dd" [(ngModel)]="dispDate" /></div>
          <div class="field"><label for="dp">Sold for (₦, 0 if scrapped)</label><input id="dp" class="input num" type="number" name="dp" [(ngModel)]="dispProceeds" /></div>
          <div class="field"><label for="dr">Money received into</label><select id="dr" class="input" name="dr" [(ngModel)]="dispInto"><option>Bank</option><option>Cash</option></select></div>
          <div class="field"><label for="dn">Note</label><input id="dn" class="input" name="dn" [(ngModel)]="dispNote" /></div>
        </form>
        <p class="muted sm">{{ dispProceeds - d.bookValue >= 0 ? 'Gain' : 'Loss' }} on disposal: {{ (dispProceeds - d.bookValue) | naira }}</p>
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="disposing.set(null)">Cancel</button>
        <button type="submit" form="df" class="btn btn-danger">Dispose and post</button>
      </ng-container>
    </app-modal>`,
  styles: `.pad { padding: .8rem 1rem; } .sm { font-size: .78rem; } .page-head { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; }
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: .6rem; margin-bottom: 1rem; }
    .kpis div { display: grid; gap: .1rem; padding: .7rem .9rem; border: 1px solid var(--line); border-radius: 10px; background: var(--paper, #fff); } .kpis span { font-size: .75rem; color: var(--muted); } .kpis b { font-family: var(--font-mono, monospace); }
    tr.gone { opacity: .6; } .compact td, .compact th { padding: .3rem .6rem; } .lines { display: grid; gap: .2rem; max-height: 40vh; overflow: auto; } .lines li { display: flex; justify-content: space-between; } .ok { color: var(--ok, #0f7a6d); }`,
})
export class FixedAssetsPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Erp2Api);
  private readonly core = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly months = MONTH_NAMES;
  protected readonly reg = signal<AssetRegister | null>(null);
  protected readonly suppliers = signal<{ id: number; name: string }[]>([]);
  protected readonly adding = signal(false);
  protected readonly running = signal(false);
  protected readonly prev = signal<DepPreview | null>(null);
  protected readonly disposing = signal<Asset | null>(null);
  protected a = this.blank();
  protected runYear = new Date().getFullYear(); protected runMonth = Math.max(1, new Date().getMonth());
  protected dispDate = today(); protected dispProceeds = 0; protected dispInto = 'Bank'; protected dispNote = '';

  ngOnInit() { void this.load(); }
  private blank() {
    return { name: '', category: 'Computer equipment', serialNumber: '', location: '', branchId: null as number | null, acquiredOn: today(), cost: 0, residualValue: 0,
      method: 'StraightLine', usefulLifeMonths: 36, ratePct: 25, paidFrom: 'Bank', supplierId: null as number | null, openingAccumulated: 0 };
  }
  private async load() { try { this.reg.set(await this.api.assets()); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected method(a: Asset) { return a.method === 'StraightLine' ? `Straight line, ${a.usefulLifeMonths} months` : a.method === 'ReducingBalance' ? `Reducing balance ${a.ratePct}%` : 'Not depreciated'; }

  protected openAdd() {
    this.a = this.blank(); this.adding.set(true);
    if (!this.suppliers().length) void this.core.suppliers({ page: 1, pageSize: 200 }).then(p => this.suppliers.set(p.items)).catch(() => {});
  }
  protected async add() {
    try {
      await this.api.addAsset({ ...this.a, cost: Number(this.a.cost), residualValue: Number(this.a.residualValue) || 0, usefulLifeMonths: Number(this.a.usefulLifeMonths) || 0,
        ratePct: Number(this.a.ratePct) || 0, openingAccumulated: Number(this.a.openingAccumulated) || 0, serialNumber: this.a.serialNumber || null, location: this.a.location || null });
      this.adding.set(false); this.toasts.ok('Asset added and posted.'); await this.load();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected openRun() { this.running.set(true); void this.preview(); }
  protected async preview() { try { this.prev.set(await this.api.depreciationPreview(Number(this.runYear), Number(this.runMonth))); } catch (e) { this.toasts.error(messageOf(e)); } }
  protected async run() {
    try { await this.api.runDepreciation(Number(this.runYear), Number(this.runMonth)); this.running.set(false); this.toasts.ok('Depreciation posted.'); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected openDispose(a: Asset) { this.dispProceeds = 0; this.dispNote = ''; this.dispDate = today(); this.disposing.set(a); }
  protected async dispose() {
    const d = this.disposing(); if (!d) return;
    if ((await this.confirm.ask({ title: `Dispose of ${d.name}?`, message: 'Its cost and depreciation leave the books and the gain or loss is posted. This can’t be undone.', confirmLabel: 'Dispose', danger: true })) === null) return;
    try { await this.api.disposeAsset(d.id, { disposedOn: this.dispDate, proceeds: Number(this.dispProceeds) || 0, receivedInto: this.dispInto, note: this.dispNote || null }); this.disposing.set(null); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
}
