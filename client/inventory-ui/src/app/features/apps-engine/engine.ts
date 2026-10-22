import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { Api, messageOf } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { Confirm, Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';
import { ActionDef, ActionResult, Data, EngineApi, FieldDef, KindDef, Overview, RecordRow } from './engine-api';

interface View { kind: KindDef; id: number | null; parentId: number | null; rec: RecordRow | null; form: Data; children: Record<string, RecordRow[]>; }
interface Option { id: number; name: string; }

/**
 * The one screen behind every record-keeping app. It draws itself from the app's definition on the server: tabs for its record types, a list with filters, a form
 * for each type, the status buttons and action buttons the definition allows, and each record's child records (an event's registrations, a course's lessons).
 */
@Component({
  selector: 'app-engine',
  imports: [FormsModule, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      @if (ov(); as o) {
        <div class="page-head"><h1>{{ o.def.name }}</h1><div class="grow"></div>
          @if (o.publicUrl !== null) { <a class="btn" [href]="o.publicUrl" target="_blank" rel="noopener">See the public page</a> }</div>
        <p class="muted lede">{{ o.def.intro }}</p>

        @if (o.cards.length) { <div class="kpis">@for (c of o.cards; track $index) { <div class="card kpi" [class.warn]="c.tone === 'warn' || c.tone === 'bad'"><span class="eyebrow">{{ c.label }}</span><div class="value">{{ c.value }}</div>@if (c.sub) { <div class="sub">{{ c.sub }}</div> }</div> }</div> }

        @if (tops().length > 1) {
          <div class="tabs" role="tablist">@for (k of tops(); track k.key) { <button type="button" role="tab" [class.on]="tab() === k.key" [attr.aria-selected]="tab() === k.key" (click)="pick(k.key)">{{ k.plural }}</button> }</div>
        }

        @if (kind(); as k) {
          <section class="card">
            <div class="toolbar">
              <input class="input search" type="search" placeholder="Search" aria-label="Search" [ngModel]="q()" (ngModelChange)="search($event)" />
              <div class="chips" role="group" aria-label="Status">
                <button type="button" [class.on]="!status()" (click)="filter('')">All</button>
                @for (s of k.statuses; track s.name) { <button type="button" [class.on]="status() === s.name" (click)="filter(s.name)">{{ s.name }}</button> }
              </div>
              <div class="grow"></div>
              <button type="button" class="btn" (click)="exportCsv()" [disabled]="!rows().length">Export</button>
              @if (!k.noCreate) { <button type="button" class="btn btn-primary" (click)="startNew(k, null)">New {{ k.label }}</button> }
            </div>
            @if (k.hint) { <p class="muted sm pad">{{ k.hint }}</p> }
            <div class="table-wrap"><table class="table">
              <thead><tr>@for (c of cols(k); track c.key) { <th [class.num]="c.type === 'money' || c.type === 'number'">{{ c.label }}</th> }<th>Status</th><th></th></tr></thead>
              <tbody>@for (r of rows(); track r.id) {
                <tr class="row" tabindex="0" (click)="open(r)" (keydown.enter)="open(r)">
                  @for (c of cols(k); track c.key; let first = $first) {
                    <td [class.num]="c.type === 'money' || c.type === 'number'" [class.strong]="first">{{ show(r, c) }}
                      @if (first) { @for (f of r.flags; track f) { <div class="flag">⚠ {{ f }}</div> } @if (r.labels['_parent']) { <div class="muted sm">{{ r.labels['_parent'] }}</div> } }</td>
                  }
                  <td><span [class]="'stamp stamp-' + tone(k, r.status)">{{ r.status }}</span></td>
                  <td class="actions" (click)="$event.stopPropagation()">@for (n of nextOf(k, r.status); track n) { <button type="button" class="btn btn-sm" (click)="move(r, n)">{{ n }}</button> }</td>
                </tr>
              }</tbody>
            </table></div>
            @if (!rows().length) { <div class="empty"><strong>Nothing here yet</strong>@if (!k.noCreate) { Use “New {{ k.label }}” to add the first one. }</div> }
          </section>
        }
      }
    </div>

    <app-modal [open]="!!cur()" [heading]="heading()" (closed)="closeAll()">
      @if (cur(); as v) {
        @if (stack().length > 1) { <button type="button" class="btn btn-sm back" (click)="back()">← Back to {{ stack()[stack().length - 2].rec?.title }}</button> }
        @if (v.rec; as r) {
          <div class="meta"><span [class]="'stamp stamp-' + tone(v.kind, r.status)">{{ r.status }}</span>
            @for (n of nextOf(v.kind, r.status); track n) { <button type="button" class="btn btn-sm" (click)="move(r, n)">Mark {{ n }}</button> }
            @for (a of actionsFor(v.kind, r.status); track a.key) { <button type="button" class="btn btn-sm" [class.btn-primary]="a.primary" [disabled]="busy()" (click)="run(r, a)">{{ a.label }}</button> }
            @for (f of r.flags; track f) { <span class="flag">⚠ {{ f }}</span> }</div>
        }
        <form id="ef" class="form-grid" (ngSubmit)="save()">
          @for (f of v.kind.fields; track f.key) {
            @if (f.type !== 'json') {
              @if (f.readOnly) {
                @if (v.rec && shown(v.rec, f)) { <div class="field"><span class="lab">{{ f.label }}</span><div class="ro">{{ shown(v.rec, f) }}</div></div> }
              } @else {
                <div class="field" [class.span-2]="f.type === 'textarea' || f.type === 'lines' || f.type === 'customers'">
                  <label [for]="'f-' + f.key">{{ f.label }}@if (f.required) { <span class="req"> *</span> }</label>
                  @switch (f.type) {
                    @case ('textarea') { <textarea class="input" rows="4" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]"></textarea> }
                    @case ('number') { <input class="input num" type="number" step="any" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> }
                    @case ('money') { <input class="input num" type="number" step="0.01" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> }
                    @case ('date') { <input class="input" type="date" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> }
                    @case ('email') { <input class="input" type="email" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> }
                    @case ('phone') { <input class="input" type="tel" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> }
                    @case ('bool') { <label class="chk"><input type="checkbox" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> Yes</label> }
                    @case ('select') { <select class="input" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]"><option [ngValue]="null">—</option>@for (o of f.options ?? []; track o) { <option [ngValue]="o">{{ o }}</option> }</select> }
                    @case ('lookup') { <select class="input" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]"><option [ngValue]="null">—</option>@for (o of options(f); track o.id) { <option [ngValue]="o.id">{{ o.name }}</option> }</select> }
                    @case ('lines') {
                      @for (l of lines(f.key); track $index; let i = $index) {
                        <div class="line"><select class="input" [name]="f.key + 'p' + i" [(ngModel)]="l.productId"><option [ngValue]="null">Choose a product…</option>@for (o of options(prodField); track o.id) { <option [ngValue]="o.id">{{ o.name }}</option> }</select>
                          <input class="input num" type="number" min="1" [name]="f.key + 'q' + i" [(ngModel)]="l.quantity" aria-label="Quantity" /><input class="input num" type="number" min="0" step="0.01" [name]="f.key + 'r' + i" [(ngModel)]="l.price" placeholder="usual price" aria-label="Price" />
                          <button type="button" class="btn btn-sm" (click)="removeLine(f.key, i)" aria-label="Remove line">×</button></div>
                      }
                      <button type="button" class="btn btn-sm" (click)="addLine(f.key)">Add a line</button>
                    }
                    @case ('customers') {
                      <div class="pickbar"><input class="input" type="search" [id]="'f-' + f.key" placeholder="Search name or phone" [ngModel]="pickQ()" (ngModelChange)="pickQ.set($event)" [ngModelOptions]="{ standalone: true }" />
                        <span class="muted sm"><strong>{{ picked(f.key).length }}</strong> selected</span>
                        <button type="button" class="btn btn-sm" (click)="pickShown(f.key, true)">Tick all shown</button><button type="button" class="btn btn-sm" (click)="pickShown(f.key, false)">Clear shown</button></div>
                      <div class="picklist" role="group" [attr.aria-label]="f.label">
                        @for (o of customersShown(); track o.id) {
                          <label class="chk pick"><input type="checkbox" [checked]="picked(f.key).includes(o.id)" (change)="toggle(f.key, o.id)" /> {{ o.name }}</label>
                        } @empty { <p class="muted sm">No customer matches.</p> }
                      </div>
                    }
                    @default { <input class="input" [id]="'f-' + f.key" [name]="f.key" [(ngModel)]="form[f.key]" /> }
                  }
                  @if (f.help) { <span class="muted sm">{{ f.help }}</span> }
                </div>
              }
            }
          }
        </form>

        @if (v.rec) {
          @for (ck of childKinds(v.kind); track ck.key) {
            <h3 class="display sub">{{ ck.plural }}</h3>
            <div class="table-wrap"><table class="table compact">
              <thead><tr>@for (c of cols(ck); track c.key) { <th>{{ c.label }}</th> }<th>Status</th></tr></thead>
              <tbody>@for (c of v.children[ck.key] || []; track c.id) {
                <tr class="row" tabindex="0" (click)="open(c)" (keydown.enter)="open(c)">@for (col of cols(ck); track col.key) { <td>{{ show(c, col) }}</td> }<td><span [class]="'stamp stamp-' + tone(ck, c.status)">{{ c.status }}</span></td></tr>
              }</tbody></table></div>
            @if (!(v.children[ck.key] || []).length) { <p class="muted sm">None yet.</p> }
            @if (!ck.noCreate) { <button type="button" class="btn btn-sm" (click)="startNew(ck, v.rec.id)">Add {{ ck.label }}</button> }
          }
        }
      }
      <ng-container modal-actions>
        @if (cur(); as v) { @if (v.rec && !v.kind.noDelete) { <button type="button" class="btn btn-danger" (click)="remove(v.rec)">Delete</button> } }
        <button type="button" class="btn" (click)="closeAll()">Close</button>
        <button type="submit" form="ef" class="btn btn-primary" [disabled]="busy()">Save</button>
      </ng-container>
    </app-modal>

    <app-modal [open]="!!result()" heading="Done" (closed)="result.set(null)">
      @if (result(); as r) {
        <p class="msg">{{ r.message }}</p>
        @if (r.url) { <p><a class="btn" [href]="r.url" target="_blank" rel="noopener">Open the link</a> <button type="button" class="btn" (click)="copy(r.url)">Copy the link</button></p> }
      }
      <ng-container modal-actions><button type="button" class="btn btn-primary" (click)="result.set(null)">Close</button></ng-container>
    </app-modal>`,
  styles: `
    .lede { margin: .2rem 0 1rem; } .page-head { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; } .sm { font-size: .78rem; } .pad { padding: 0 1rem; }
    .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: .8rem; margin-bottom: 1rem; }
    .tabs { display: flex; gap: .3rem; margin-bottom: .8rem; flex-wrap: wrap; } .tabs button, .chips button { border: 1px solid var(--line, #dde1ea); background: var(--paper, #fff); padding: .4rem .85rem; border-radius: 999px; cursor: pointer; font: inherit; font-size: .85rem; }
    .tabs button.on, .chips button.on { background: var(--brand, #0f1e3d); color: #fff; border-color: var(--brand, #0f1e3d); }
    .toolbar { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; } .search { max-width: 14rem; } .chips { display: flex; gap: .25rem; flex-wrap: wrap; }
    tr.row { cursor: pointer; } .actions { white-space: nowrap; display: flex; gap: .25rem; } .flag { color: #b3261e; font-size: .78rem; font-weight: 600; }
    .meta { display: flex; gap: .4rem; flex-wrap: wrap; align-items: center; margin-bottom: .8rem; } .back { margin-bottom: .6rem; } .req { color: #b3261e; }
    .ro { padding: .45rem 0; } .chk { display: inline-flex; gap: .4rem; align-items: center; font-weight: 400; } .pickbar { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; } .pickbar .input { flex: 1 1 14rem; }
    .picklist { max-height: 16rem; overflow: auto; border: 1px solid var(--line, #ddd); border-radius: 8px; padding: .3rem .6rem; margin-top: .4rem; display: grid; grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr)); }
    .pick { padding: .15rem 0; }
    .line { display: grid; grid-template-columns: 1fr 5rem 7rem auto; gap: .4rem; margin-bottom: .4rem; }
    .msg { white-space: pre-wrap; } .compact td, .compact th { padding: .3rem .5rem; } h3.sub { margin: 1.2rem 0 .4rem; }`,
})
export class EnginePage {
  private readonly api = inject(EngineApi);
  private readonly core = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  private readonly app = toSignal(inject(ActivatedRoute).paramMap.pipe(map(p => p.get('app') ?? '')), { initialValue: '' });

  protected readonly ov = signal<Overview | null>(null);
  protected readonly tab = signal('');
  protected readonly rows = signal<RecordRow[]>([]);
  protected readonly status = signal('');
  protected readonly q = signal('');
  protected readonly stack = signal<View[]>([]);
  protected readonly busy = signal(false);
  protected readonly result = signal<ActionResult | null>(null);
  protected readonly lookups = signal<Record<string, Option[]>>({});
  protected form: Data = {};
  /** A stand-in field so product lines can use the product list. */
  protected readonly prodField: FieldDef = { key: 'p', label: 'Product', type: 'lookup', required: false, options: null, lookup: 'product', help: null, inList: false, default: null, readOnly: false };
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly tops = computed(() => this.ov()?.def.kinds.filter(k => !k.parent) ?? []);
  protected readonly kind = computed(() => this.ov()?.def.kinds.find(k => k.key === this.tab()) ?? null);
  protected readonly cur = computed(() => this.stack().at(-1) ?? null);
  protected readonly heading = computed(() => { const v = this.cur(); return v ? (v.rec ? v.rec.title : `New ${v.kind.label}`) : ''; });

  constructor() {
    effect(() => { const a = this.app(); untracked(() => void this.loadApp(a)); });
  }

  private async loadApp(app: string) {
    this.ov.set(null); this.rows.set([]); this.stack.set([]); this.status.set(''); this.q.set('');
    if (!app) return;
    try {
      const o = await this.api.overview(app); this.ov.set(o);
      this.tab.set(o.def.kinds.find(k => !k.parent)?.key ?? '');
      await this.reload();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  private async reload() {
    const k = this.kind(); const o = this.ov(); if (!k || !o) return;
    try { this.rows.set(await this.api.list(o.def.key, k.key, { status: this.status(), q: this.q() })); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  private async refreshCards() { const o = this.ov(); if (o) try { this.ov.set(await this.api.overview(o.def.key)); } catch { /* the figures are a convenience */ } }
  protected pick(k: string) { this.tab.set(k); this.status.set(''); this.q.set(''); void this.reload(); }
  protected filter(s: string) { this.status.set(s); void this.reload(); }
  protected search(v: string) { this.q.set(v); if (this.timer) clearTimeout(this.timer); this.timer = setTimeout(() => void this.reload(), 300); }

  // ---- reading a record
  protected cols(k: KindDef): FieldDef[] { const t = k.fields.find(f => f.key === 'title'); return [t ?? { ...this.prodField, key: 'title', label: 'Title', type: 'text' }, ...k.fields.filter(f => f.inList && f.key !== 'title')]; }
  private raw(r: RecordRow, key: string) { return key === 'title' ? r.title : key === 'code' ? r.code : key === 'date' ? r.date : key === 'amount' ? r.amount : r.data[key]; }
  protected show(r: RecordRow, f: FieldDef): string {
    if (f.type === 'lookup') return r.labels[f.key] ?? '';
    const v = this.raw(r, f.key);
    if (v === null || v === undefined || v === '') return '';
    if (f.type === 'bool') return v ? '✓' : '';
    if (f.type === 'customers') return Array.isArray(v) && v.length ? `${v.length} customer(s)` : '';
    if (f.type === 'money') return '₦' + Number(v).toLocaleString('en-NG', { maximumFractionDigits: 2 });
    return String(v);
  }
  protected shown(r: RecordRow, f: FieldDef): string { return this.show(r, f); }
  protected tone(k: KindDef, status: string) { return k.statuses.find(s => s.name === status)?.tone ?? 'info'; }
  protected nextOf(k: KindDef, status: string): string[] { return k.statuses.find(s => s.name === status)?.next ?? []; }
  protected actionsFor(k: KindDef, status: string): ActionDef[] { return (k.actions ?? []).filter(a => !a.inStatuses || a.inStatuses.includes(status)); }
  protected childKinds(k: KindDef): KindDef[] { return this.ov()?.def.kinds.filter(c => c.parent === k.key) ?? []; }
  protected lines(key: string): { productId: number | null; quantity: number; price: number | null }[] { return (this.form[key] ??= []); }
  protected addLine(key: string) { this.lines(key).push({ productId: null, quantity: 1, price: null }); }
  protected removeLine(key: string, i: number) { this.lines(key).splice(i, 1); }
  protected options(f: FieldDef): Option[] { return this.lookups()[f.lookup ?? ''] ?? []; }

  // ---- picking several customers (a broadcast's "Selected customers")
  protected readonly pickQ = signal('');
  protected readonly customersShown = computed(() => {
    const words = this.pickQ().toLowerCase().split(/\s+/).filter(Boolean);
    const all = this.lookups()['customer'] ?? [];
    return words.length ? all.filter(o => words.every(w => o.name.toLowerCase().includes(w) || o.name.replace(/\D/g, '').includes(w.replace(/\D/g, '') || '\u0000'))) : all;
  });
  protected picked(key: string): number[] { return (this.form[key] ??= []) as number[]; }
  protected toggle(key: string, id: number) { const p = this.picked(key); const i = p.indexOf(id); if (i >= 0) p.splice(i, 1); else p.push(id); }
  protected pickShown(key: string, on: boolean) {
    const p = this.picked(key); const shown = this.customersShown().map(o => o.id);
    this.form[key] = on ? [...new Set([...p, ...shown])] : p.filter(id => !shown.includes(id));
  }

  // ---- lookups for the open form
  private async ensureLookups(k: KindDef) {
    const wanted = new Set(k.fields.filter(f => f.type === 'lookup' && f.lookup).map(f => f.lookup!));
    if (k.fields.some(f => f.type === 'lines')) wanted.add('product');
    if (k.fields.some(f => f.type === 'customers')) wanted.add('customer');
    const o = this.ov(); if (!o) return;
    for (const w of wanted) {
      if (w === 'product' && this.lookups()['product']) continue;
      try {
        let list: Option[] = [];
        if (w === 'customer') list = await this.allCustomers();
        else if (w === 'employee') list = (await this.api2.employees()).filter(e => e.isActive).map(e => ({ id: e.id, name: e.fullName }));
        else if (w === 'product') list = (await this.core.products({ pageSize: 200 })).items.map(p => ({ id: p.id, name: `${p.name} (${p.sku})` }));
        else if (w.startsWith('record:')) list = (await this.api.list(o.def.key, w.slice(7))).map(r => ({ id: r.id, name: r.title }));
        this.lookups.update(l => ({ ...l, [w]: list }));
      } catch { /* the list stays empty; the form says so */ }
    }
  }

  /** Every customer (page by page), with the phone so two of the same name can be told apart. */
  private async allCustomers(): Promise<Option[]> {
    const out: Option[] = [];
    for (let page = 1; page <= 50; page++) {
      const r = await this.core.customers({ page, pageSize: 200 });
      out.push(...r.items.map(c => ({ id: c.id, name: c.phone ? `${c.name} · ${c.phone}` : c.name })));
      if (r.items.length < 200 || out.length >= r.total) break;
    }
    return out;
  }

  // ---- opening, saving
  private blank(k: KindDef): Data {
    const f: Data = {};
    for (const d of k.fields) f[d.key] = d.type === 'bool' ? false : d.type === 'lines' || d.type === 'customers' ? [] : d.default ?? null;
    return f;
  }
  private fill(k: KindDef, r: RecordRow): Data {
    const f: Data = {};
    for (const d of k.fields) { const v = this.raw(r, d.key); f[d.key] = d.type === 'bool' ? !!v : d.type === 'lines' ? ((v as unknown[]) ?? []).map(x => ({ ...(x as object) })) : d.type === 'customers' ? [...((v as number[]) ?? [])] : v ?? null; }
    return f;
  }
  protected async startNew(k: KindDef, parent: number | null) {
    await this.ensureLookups(k);
    const v: View = { kind: k, id: null, parentId: parent, rec: null, form: this.blank(k), children: {} };
    this.stack.update(s => [...s, v]); this.form = v.form;
  }
  protected async open(r: RecordRow) {
    const o = this.ov(); if (!o) return;
    const k = o.def.kinds.find(x => x.key === r.kind)!;
    try {
      await this.ensureLookups(k);
      const fresh = await this.api.get(o.def.key, r.id);
      const children: Record<string, RecordRow[]> = {};
      for (const ck of this.childKinds(k)) children[ck.key] = await this.api.list(o.def.key, ck.key, { parent: r.id });
      const v: View = { kind: k, id: r.id, parentId: r.parentId, rec: fresh, form: this.fill(k, fresh), children };
      this.stack.update(s => [...s, v]); this.form = v.form;
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected back() { this.stack.update(s => s.slice(0, -1)); this.form = this.cur()?.form ?? {}; if (this.cur()?.rec) void this.refreshTop(); }
  protected closeAll() { this.stack.set([]); this.form = {}; }
  private async refreshTop() {
    const v = this.cur(); const o = this.ov(); if (!v?.rec || !o) return;
    try {
      const fresh = await this.api.get(o.def.key, v.rec.id);
      const children: Record<string, RecordRow[]> = {};
      for (const ck of this.childKinds(v.kind)) children[ck.key] = await this.api.list(o.def.key, ck.key, { parent: v.rec.id });
      const nv: View = { ...v, rec: fresh, children, form: this.fill(v.kind, fresh) };
      this.stack.update(s => [...s.slice(0, -1), nv]); this.form = nv.form;
    } catch { /* leave the screen as it is */ }
  }

  protected async save() {
    const v = this.cur(); const o = this.ov(); if (!v || !o) return;
    const fields: Data = {};
    for (const f of v.kind.fields) {
      if (f.readOnly || f.type === 'json') continue;
      const x = this.form[f.key];
      if (f.type === 'bool') fields[f.key] = !!x;
      else if (f.type === 'lines') fields[f.key] = ((x as { productId: number | null; quantity: number; price: number | null }[]) ?? []).filter(l => l.productId).map(l => ({ productId: l.productId, quantity: Number(l.quantity), price: l.price === null || l.price === undefined || (l.price as unknown) === '' ? null : Number(l.price) }));
      else if (x === null || x === undefined || x === '') fields[f.key] = null;
      else fields[f.key] = f.type === 'number' || f.type === 'money' ? Number(x) : typeof x === 'string' ? x.trim() : x;
    }
    this.busy.set(true);
    try {
      const body = { parentId: v.parentId, fields };
      const id = v.id ? (await this.api.update(o.def.key, v.kind.key, v.id, body)).id : (await this.api.create(o.def.key, v.kind.key, body)).id;
      this.toasts.ok('Saved.');
      await this.reload(); void this.refreshCards();
      if (this.stack().length > 1) { this.stack.update(s => s.slice(0, -1)); this.form = this.cur()?.form ?? {}; await this.refreshTop(); }
      else if (!v.id) { this.stack.set([]); const r = await this.api.get(o.def.key, id); await this.open(r); }
      else { this.stack.set([]); this.form = {}; }
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async move(r: RecordRow, status: string) {
    const o = this.ov(); if (!o) return;
    try { await this.api.setStatus(o.def.key, r.id, status); await this.reload(); void this.refreshCards(); if (this.cur()?.rec?.id === r.id) await this.refreshTop(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async run(r: RecordRow, a: ActionDef) {
    const o = this.ov(); if (!o) return;
    let text: string | null = null;
    if (a.prompt) { text = await this.confirm.ask({ title: a.label, message: a.confirm ?? '', confirmLabel: a.label, reason: { label: a.prompt, required: true } }); if (text === null) return; }
    else if (a.confirm && (await this.confirm.ask({ title: a.label, message: a.confirm, confirmLabel: a.label })) === null) return;
    this.busy.set(true);
    try {
      const res = await this.api.action(o.def.key, r.id, a.key, text);
      await this.reload(); void this.refreshCards(); await this.refreshTop();
      if (res.message && res.message.length <= 110 && !res.url && !res.message.includes('\n')) this.toasts.ok(res.message); else this.result.set(res);
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async remove(r: RecordRow) {
    const o = this.ov(); if (!o) return;
    if ((await this.confirm.ask({ title: `Delete ${r.title}?`, message: 'It is removed for good, along with anything that belongs to it.', confirmLabel: 'Delete', danger: true })) === null) return;
    try {
      await this.api.remove(o.def.key, r.id); this.toasts.ok('Deleted.');
      if (this.stack().length > 1) { this.stack.update(s => s.slice(0, -1)); this.form = this.cur()?.form ?? {}; await this.refreshTop(); } else this.closeAll();
      await this.reload(); void this.refreshCards();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected copy(text: string) { void navigator.clipboard?.writeText(text).then(() => this.toasts.ok('Link copied.'), () => this.toasts.error('Copy the link by hand.')); }

  protected exportCsv() {
    const k = this.kind(); if (!k) return;
    const cols = [...this.cols(k), { key: 'status', label: 'Status', type: 'text' } as FieldDef];
    const cell = (s: string) => `"${s.replace(/"/g, '""')}"`;
    const lines = [cols.map(c => cell(c.label)).join(','), ...this.rows().map(r => cols.map(c => cell(c.key === 'status' ? r.status : this.show(r, c))).join(','))];
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    a.download = `${this.ov()?.def.key}-${k.key}.csv`; a.click(); URL.revokeObjectURL(a.href);
  }
}
