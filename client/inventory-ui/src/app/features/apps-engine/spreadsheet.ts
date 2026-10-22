import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { EngineApi, RecordRow } from './engine-api';
import { COLS, evaluateAll, keyOf, show } from './formula';

const ROWS = 40;

/** Spreadsheet: sheets with formulas, and =FIG("name") for live figures from your own records. */
@Component({
  selector: 'app-spreadsheet',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Spreadsheet</h1><div class="grow"></div><button type="button" class="btn btn-primary" (click)="create()">New sheet</button></div>
      <p class="muted lede">Type numbers, text and formulas such as =B2*C2 or =SUM(B2:B9). =FIG("receivables") brings in a live figure.</p>
      <div class="layout">
        <aside class="card">
          <ul class="sheets">@for (s of sheets(); track s.id) { <li><button type="button" [class.on]="s.id === current()?.id" (click)="open(s.id)">{{ s.title }}</button></li> }</ul>
          @if (!sheets().length) { <p class="muted sm pad">No sheets yet.</p> }
          <details class="figs"><summary>Live figures</summary>
            <ul>@for (f of figNames(); track f.name) { <li><code>=FIG("{{ f.name }}")</code><br><span class="muted sm">{{ f.label }}: {{ figValue(f.name) }}</span></li> }</ul></details>
        </aside>
        <section class="card main">
          @if (current(); as c) {
            <div class="bar"><input class="input title" aria-label="Sheet name" [ngModel]="title()" (ngModelChange)="title.set($event); dirty.set(true)" />
              <span class="muted sm">{{ dirty() ? 'Unsaved changes' : 'Saved' }}</span><div class="grow"></div>
              <button type="button" class="btn" (click)="exportCsv()">Export</button><button type="button" class="btn btn-danger" (click)="remove()">Delete</button>
              <button type="button" class="btn btn-primary" [disabled]="!dirty() || busy()" (click)="save()">Save</button></div>
            <div class="grid"><table>
              <thead><tr><th></th>@for (c of cols; track c) { <th>{{ c }}</th> }</tr></thead>
              <tbody>@for (r of rows; track r) {
                <tr><th>{{ r + 1 }}</th>@for (c of cols; track c; let ci = $index) {
                  <td><input [attr.aria-label]="key(ci, r)" [class.err]="isErr(key(ci, r))" [class.num]="isNum(key(ci, r))" [value]="focus() === key(ci, r) ? (cells()[key(ci, r)] || '') : display(key(ci, r))"
                    (focus)="focus.set(key(ci, r))" (blur)="focus.set('')" (input)="edit(key(ci, r), $any($event.target).value)" /></td>
                }</tr>
              }</tbody></table></div>
          } @else { <div class="empty"><strong>Open a sheet, or start a new one</strong></div> }
        </section>
      </div>
    </div>`,
  styles: `.page-head { display: flex; gap: .6rem; align-items: center; } .sm { font-size: .78rem; } .pad { padding: 0 1rem; }
    .layout { display: grid; grid-template-columns: 14rem minmax(0, 1fr); gap: 1rem; align-items: start; } @media (max-width: 800px) { .layout { grid-template-columns: 1fr; } }
    .sheets { list-style: none; margin: 0; padding: .5rem; display: grid; gap: .2rem; } .sheets button { width: 100%; text-align: left; border: 0; background: none; padding: .45rem .6rem; border-radius: 8px; cursor: pointer; font: inherit; }
    .sheets button.on { background: var(--brand, #0f1e3d); color: #fff; } .figs { padding: .5rem 1rem 1rem; font-size: .85rem; } .figs ul { list-style: none; padding: 0; display: grid; gap: .5rem; } code { font-size: .75rem; }
    .bar { display: flex; gap: .5rem; align-items: center; flex-wrap: wrap; padding: .6rem; } .title { max-width: 18rem; font-weight: 600; }
    .grid { overflow: auto; max-height: 68vh; } table { border-collapse: collapse; font-size: .85rem; } th { position: sticky; top: 0; background: var(--sack, #f3f4f7); font-weight: 600; padding: .2rem .5rem; border: 1px solid var(--line, #dde1ea); min-width: 2rem; }
    tbody th { left: 0; text-align: center; } td { padding: 0; border: 1px solid var(--line, #dde1ea); } td input { width: 7.5rem; border: 0; padding: .25rem .4rem; font: inherit; background: transparent; } td input:focus { outline: 2px solid var(--brand, #0f1e3d); outline-offset: -2px; background: #fff; }
    td input.num { text-align: right; } td input.err { color: #b3261e; }`,
})
export class SpreadsheetPage implements OnInit {
  private readonly api = inject(EngineApi);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly cols = COLS;
  protected readonly rows = Array.from({ length: ROWS }, (_, i) => i);
  protected readonly sheets = signal<RecordRow[]>([]);
  protected readonly current = signal<RecordRow | null>(null);
  protected readonly title = signal('');
  protected readonly cells = signal<Record<string, string>>({});
  protected readonly focus = signal('');
  protected readonly dirty = signal(false);
  protected readonly busy = signal(false);
  protected readonly figNames = signal<{ name: string; label: string }[]>([]);
  private readonly figs = signal<Record<string, number>>({});
  protected readonly values = computed(() => evaluateAll(this.cells(), n => this.figs()[n]));

  async ngOnInit() {
    try {
      const f = await this.api.figures(); this.figNames.set(f.names); this.figs.set(f.values);
      await this.load();
      const first = this.sheets()[0]; if (first) await this.open(first.id);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  private async load() { this.sheets.set(await this.api.list('spreadsheet', 'sheet')); }
  protected key(c: number, r: number) { return keyOf(c, r); }
  protected display(k: string) { return show(this.values()[k]); }
  protected isErr(k: string) { const v = this.values()[k]; return typeof v === 'string' && v.startsWith('#'); }
  protected isNum(k: string) { return typeof this.values()[k] === 'number'; }
  protected figValue(n: string) { const v = this.figs()[n]; return v === undefined ? '' : v.toLocaleString('en-NG', { maximumFractionDigits: 2 }); }
  protected edit(k: string, v: string) { this.cells.update(c => { const n = { ...c }; if (v === '') delete n[k]; else n[k] = v; return n; }); this.dirty.set(true); }

  protected async open(id: number) {
    if (this.dirty() && (await this.confirm.ask({ title: 'Leave without saving?', message: 'This sheet has changes that are not saved.', confirmLabel: 'Leave', danger: true })) === null) return;
    try {
      const r = await this.api.get('spreadsheet', id);
      this.current.set(r); this.title.set(r.title);
      this.cells.set({ ...(((r.data['grid'] as { cells?: Record<string, string> } | undefined)?.cells) ?? {}) }); this.dirty.set(false);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async create() {
    try {
      const { id } = await this.api.create('spreadsheet', 'sheet', { parentId: null, fields: { title: 'Untitled sheet', grid: { cells: {} } } });
      this.dirty.set(false); await this.load(); await this.open(id);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async save() {
    const c = this.current(); if (!c) return;
    this.busy.set(true);
    try { await this.api.update('spreadsheet', 'sheet', c.id, { parentId: null, fields: { title: this.title().trim() || 'Untitled sheet', grid: { cells: this.cells() } } }); this.dirty.set(false); await this.load(); this.toasts.ok('Saved.'); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }
  protected async remove() {
    const c = this.current(); if (!c) return;
    if ((await this.confirm.ask({ title: `Delete ${c.title}?`, message: 'The sheet is removed for good.', confirmLabel: 'Delete', danger: true })) === null) return;
    try { await this.api.remove('spreadsheet', c.id); this.current.set(null); this.dirty.set(false); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected exportCsv() {
    const cell = (s: string) => `"${s.replace(/"/g, '""')}"`;
    let last = 0; for (const k of Object.keys(this.cells())) last = Math.max(last, Number(k.slice(1)));
    const lines = Array.from({ length: last }, (_, r) => COLS.map((_c, c) => cell(show(this.values()[keyOf(c, r)]).replace(/,(?=\d{3})/g, ''))).join(','));
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    a.download = `${this.title() || 'sheet'}.csv`; a.click(); URL.revokeObjectURL(a.href);
  }
}
