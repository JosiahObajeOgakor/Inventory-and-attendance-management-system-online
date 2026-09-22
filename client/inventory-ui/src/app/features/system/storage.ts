import { HttpClient } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { StampTimePipe } from '../../shared/ui';

interface Usage { usedMB: number; limitMB: number; percent: number; warn: boolean; tables: { table: string; rows: number; mb: number }[]; }
interface PreviewRow { table: string; rows: number; }
interface ArchiveFile { name: string; bytes: number; createdUtc: string; }

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/** Database size, and archiving of old settled records (the desktop "Database storage & archive" window). */
@Component({
  selector: 'app-storage',
  imports: [FormsModule, StampTimePipe, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Database storage</h1></div>
      <p class="muted lede">See how much space the records use, and move old, fully settled records into a file you keep. Balances, stock levels and loan balances are never changed by archiving.</p>

      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (usage(); as u) {
        <section class="card card-pad">
          <div class="head"><strong>{{ u.usedMB | number }} MB used</strong>
            @if (u.limitMB > 0) { <span class="muted">of {{ u.limitMB | number }} MB — {{ u.percent }}% full</span> } @else { <span class="muted">no size limit set (the server disk is the limit)</span> }</div>
          @if (u.limitMB > 0) { <div class="meter" role="meter" [attr.aria-valuenow]="u.percent" aria-valuemin="0" aria-valuemax="100"><i [class.warn]="u.warn" [style.width.%]="Math.min(100, u.percent)"></i></div> }
          @if (u.warn) { <p class="notice">This database is getting full. Archive old records below and keep the file somewhere safe.</p> }
          <table class="table small"><thead><tr><th>Biggest tables</th><th class="num">Rows</th><th class="num">MB</th></tr></thead>
            <tbody>@for (t of u.tables; track t.table) { <tr><td class="mono">{{ t.table }}</td><td class="num mono">{{ t.rows | number }}</td><td class="num mono">{{ t.mb }}</td></tr> }</tbody></table>
        </section>
      }

      <section class="card card-pad">
        <h2 class="display sub">Archive old records</h2>
        <div class="row">
          <label for="cut">Archive settled records dated before</label>
          <input id="cut" class="input" type="date" [max]="latest" [ngModel]="cutoff()" (ngModelChange)="cutoff.set($event); preview.set(null)" />
          <button type="button" class="btn" [disabled]="busy()" (click)="doPreview()">See what would be archived</button>
        </div>
        <p class="muted sm">Always kept, however old: unpaid or part-paid invoices, unpaid purchase orders, open staff loans, unredeemed rebates, and the stock movements of any invoice that is kept. Nothing from the last 90 days is ever archived.</p>

        @if (preview(); as p) {
          <table class="table small"><thead><tr><th>Record type</th><th class="num">Would be archived</th></tr></thead>
            <tbody>@for (r of p; track r.table) { <tr><td class="mono">{{ r.table }}</td><td class="num mono">{{ r.rows | number }}</td></tr> }</tbody></table>
          <div class="go">
            <span>{{ total() | number }} records.</span>
            <button type="button" class="btn btn-danger" [disabled]="busy() || total() === 0" (click)="archive()">Archive them…</button>
          </div>
          <p class="muted sm">They are saved to a ZIP (an Excel workbook plus one CSV per table) on the server first. If that fails, nothing is removed.</p>
        }
      </section>

      <section class="card card-pad">
        <h2 class="display sub">Archive files</h2>
        @for (f of files(); track f.name) {
          <div class="file"><a [href]="'/api/admin/archive/files/' + f.name" download>{{ f.name }}</a><span class="muted">{{ (f.bytes / 1048576).toFixed(2) }} MB · {{ f.createdUtc | stampTime }}</span></div>
        } @empty { <p class="muted">No archives yet.</p> }
        <p class="muted sm">Download a copy of each archive and store it off the server too. The server keeps them in its archive folder, which your backups include.</p>
      </section>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 46rem; } .sm { font-size: .78rem; } section { margin-bottom: 1rem; } .sub { font-size: 1.4rem; margin: 0 0 .8rem; }
    .head { display: flex; gap: .8rem; align-items: baseline; flex-wrap: wrap; } .meter { height: 10px; background: #e6ebe8; border-radius: 99px; overflow: hidden; margin: .6rem 0; } .meter i { display: block; height: 100%; background: var(--brand); } .meter i.warn { background: var(--stamp); }
    .small { font-size: .84rem; margin-top: .6rem; } .row { display: flex; gap: .7rem; align-items: center; flex-wrap: wrap; } .row label { margin: 0; } .go { display: flex; gap: 1rem; align-items: center; margin: .8rem 0 .3rem; }
    .file { display: flex; justify-content: space-between; gap: 1rem; padding: .5rem 0; border-bottom: 1px solid var(--line); flex-wrap: wrap; }
  `,
})
export class StoragePage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly Math = Math;
  protected readonly latest = iso(new Date(Date.now() - 91 * 864e5));
  protected readonly cutoff = signal(iso(new Date(new Date().getFullYear() - 2, new Date().getMonth(), new Date().getDate())));
  protected readonly usage = signal<Usage | null>(null);
  protected readonly preview = signal<PreviewRow[] | null>(null);
  protected readonly files = signal<ArchiveFile[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected total() { return (this.preview() ?? []).reduce((s, r) => s + r.rows, 0); }

  ngOnInit() { void this.load(); }

  private async load() {
    try {
      this.usage.set(await firstValueFrom(this.http.get<Usage>('/api/admin/archive/usage')));
      this.files.set(await firstValueFrom(this.http.get<ArchiveFile[]>('/api/admin/archive/files')));
    } catch (e) { this.error.set(messageOf(e)); }
  }

  protected async doPreview() {
    this.busy.set(true); this.error.set('');
    try { this.preview.set(await firstValueFrom(this.http.get<PreviewRow[]>('/api/admin/archive/preview', { params: { cutoff: this.cutoff() } }))); }
    catch (e) { this.error.set(messageOf(e)); this.preview.set(null); } finally { this.busy.set(false); }
  }

  protected async archive() {
    const typed = await this.confirm.ask({
      title: 'Archive these records?', danger: true, confirmLabel: 'Archive and remove',
      message: `${this.total().toLocaleString()} settled records dated before ${this.cutoff()} will be saved to a file and then removed from the database. This can't be undone from here (the file keeps a copy).`,
      reason: { label: 'Type ARCHIVE to confirm', required: true },
    });
    if (typed === null) return;
    this.busy.set(true); this.error.set('');
    try {
      const r = await firstValueFrom(this.http.post<{ records: number; file: string }>('/api/admin/archive', { cutoff: this.cutoff(), confirm: typed }));
      this.toasts.ok(`Archived ${r.records.toLocaleString()} records to ${r.file}.`); this.preview.set(null); await this.load();
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
