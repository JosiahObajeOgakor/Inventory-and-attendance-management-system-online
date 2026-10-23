import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { StampTimePipe } from '../../shared/ui';

interface Campaign {
  id: number; type: string; name: string; description: string; enabled: boolean; sendWhatsApp: boolean; sendEmail: boolean; runTime: string; everyNDays: number;
  startDate: string | null; endDate: string | null; cooldownDays: number; maxRecipients: number; thresholdDays: number; fallbackRepurchaseDays: number;
  lastRunOn: string | null; lastRunAt: string | null; lastRunSent: number; lastRunNote: string | null; forAdmin: boolean;
  /** A digital marketer's proposed settings, waiting for an admin. */
  pendingBy: string | null; pendingAt: string | null; pending: { enabled: boolean; runTime: string; everyNDays: number; maxRecipients: number; sendWhatsApp: boolean; sendEmail: boolean } | null;
}
interface RunResult { planned: number; sent: number; failed: number; skipped: number; samples: string[]; note: string; }
interface Consent { optedIn: number; optedOut: number; unknown: number; }
interface FunnelStep { stage: string; whatsApp: number; website: number; }
interface DropOff { stage: string; whatsApp: number; website: number; reachable: number; campaign: string | null; }
interface Slice { name: string; count: number; }
interface Insights {
  days: number; conversations: number; customerMessages: number; messagesPerConversation: number; aiCalls: number; quotes: number; quotesRefused: number; handoffs: number;
  website?: { views: number; basketsStarted: number; basketsSent: number; checkouts: number; checkoutValue: number } | null;
  sales: number; salesValue: number; conversionPct: number; funnel: FunnelStep[]; dropOffs: DropOff[]; languages: Slice[]; countries: Slice[]; networks: Slice[]; webRegions: Slice[]; messagesByHour: number[];
}
const STEP: Record<string, string> = { started: 'Started a chat', browsed: 'Looked at products', selected: 'Picked products', summary: 'Saw the total', order: 'Got a payment account', sale: 'Paid' };
const DROP: Record<string, string> = { started: 'Said hello, then left', browsed: 'Looked, picked nothing', selected: 'Picked, left before the total', summary: 'Saw the total, didn\'t say yes', order: 'Got an account, didn\'t pay' };
const LANG: Record<string, string> = { en: 'English', pcm: 'Pidgin', ig: 'Igbo', ha: 'Hausa', yo: 'Yoruba', igl: 'Igala' };
interface LogRow { id: number; campaign: string; customer: string; recipient: string; channel: string; language: string; status: string; detail: string | null; mode: string | null; text: string; at: string; }

/**
 * Campaigns: reminders, offers and restock alerts that write themselves from live stock and each customer's own buying history, and go out on the days and times set here.
 * The admin decides WHAT KIND of message runs and WHEN; the wording is drafted fresh every run, so no message needs approving one by one.
 */
@Component({
  selector: 'app-campaigns',
  imports: [FormsModule, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Campaigns</h1></div>
      <p class="muted lede">Switch a campaign on, choose the time and how often, and it runs by itself. Each message is written fresh from today's stock and the customer's own purchases — nothing is mass-produced, nothing needs approving one by one.
        Only customers who said yes in the WhatsApp chat (or later subscribed) are ever messaged, never twice inside the cooldown, never before 07:00 or after 20:30, and STOP always works.</p>

      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      @if (consent(); as c) {
        <p class="stats"><strong>{{ c.optedIn }}</strong> customers have agreed to messages · <strong>{{ c.optedOut }}</strong> opted out · <strong>{{ c.unknown }}</strong> not asked yet (they are asked on their next WhatsApp order)</p>
      }

      @if (insights(); as s) {
        <section class="card card-pad" aria-labelledby="ins-h">
          <div class="top">
            <h2 id="ins-h" class="display sub">Who is chatting, and where they drop off</h2>
            <label class="inline sm">Last <select class="input" [ngModel]="days()" (ngModelChange)="setDays($event)" aria-label="Period">
              <option [ngValue]="7">7 days</option><option [ngValue]="30">30 days</option><option [ngValue]="90">90 days</option></select></label>
          </div>
          <p class="stats sm"><strong>{{ s.conversations }}</strong> chats · <strong>{{ s.customerMessages }}</strong> customer messages ({{ s.messagesPerConversation }} per chat) ·
            <strong>{{ s.sales }}</strong> paid ({{ s.conversionPct }}%) · <strong>{{ s.quotes }}</strong> quotations, {{ s.quotesRefused }} refused by the once-a-day limit ·
            {{ s.handoffs }} handed to a person · <strong>{{ s.aiCalls }}</strong> AI calls on open chats</p>
          @if (s.website; as w) {
            <p class="stats sm">Website: <strong>{{ w.views }}</strong> visits · <strong>{{ w.basketsStarted }}</strong> baskets started ({{ pct(w.basketsStarted, w.views) || '0%' }} of visits) ·
              <strong>{{ w.basketsSent }}</strong> sent to WhatsApp · <strong>{{ w.checkouts }}</strong> online checkouts opened (₦{{ w.checkoutValue.toLocaleString('en-NG') }})</p>
          }

          <div class="cols">
            <div>
              <h3 class="h3">How far people get</h3>
              <table class="table small">
                <thead><tr><th>Step</th><th class="r">WhatsApp</th><th class="r">Website</th></tr></thead>
                <tbody>
                  @for (f of s.funnel; track f.stage; let i = $index) {
                    <tr><td>{{ stepName(f.stage) }}</td>
                      <td class="r">{{ f.whatsApp }}@if (i > 0) { <span class="muted"> {{ pct(f.whatsApp, s.funnel[i - 1].whatsApp) }}</span> }</td>
                      <td class="r">{{ f.website }}@if (i > 0) { <span class="muted"> {{ pct(f.website, s.funnel[i - 1].website) }}</span> }</td></tr>
                  }
                </tbody>
              </table>
            </div>
            <div>
              <h3 class="h3">Where they stopped (did not buy)</h3>
              <table class="table small">
                <thead><tr><th>Stopped at</th><th class="r">WhatsApp</th><th class="r">Website</th><th class="r">We may message</th><th></th></tr></thead>
                <tbody>
                  @for (d of s.dropOffs; track d.stage) {
                    <tr><td>{{ dropName(d.stage) }}</td><td class="r">{{ d.whatsApp }}</td><td class="r">{{ d.website }}</td><td class="r">{{ d.reachable }}</td>
                      <td>@if (d.campaign && d.reachable > 0) { <button type="button" class="btn btn-sm" (click)="target(d.campaign)">Follow up</button> }</td></tr>
                  }
                </tbody>
              </table>
              <p class="muted sm">"We may message" counts people we know (a number on record) who agreed to reminders. Website visitors are only reachable once they give their WhatsApp number in the chat.</p>
            </div>
          </div>

          <div class="cols four">
            <div><h3 class="h3">Language</h3>@for (x of s.languages; track x.name) { <p class="row"><span>{{ langName(x.name) }}</span><strong>{{ x.count }}</strong></p> } @empty { <p class="muted sm">No chats yet.</p> }</div>
            <div><h3 class="h3">WhatsApp: country</h3>@for (x of s.countries; track x.name) { <p class="row"><span>{{ x.name }}</span><strong>{{ x.count }}</strong></p> } @empty { <p class="muted sm">None.</p> }</div>
            <div><h3 class="h3">Nigerian network</h3>@for (x of s.networks; track x.name) { <p class="row"><span>{{ x.name }}</span><strong>{{ x.count }}</strong></p> } @empty { <p class="muted sm">None.</p> }
              <p class="muted sm">By number prefix; ported numbers may differ.</p></div>
            <div><h3 class="h3">Website: time zone</h3>@for (x of s.webRegions; track x.name) { <p class="row"><span>{{ x.name }}</span><strong>{{ x.count }}</strong></p> } @empty { <p class="muted sm">None.</p> }</div>
          </div>

          <h3 class="h3">When customers write (Lagos time)</h3>
          <div class="hours" role="img" [attr.aria-label]="hoursLabel(s.messagesByHour)">
            @for (n of s.messagesByHour; track $index) {
              <div class="bar" [title]="$index + ':00 – ' + n + ' messages'"><span [style.height.%]="barHeight(n, s.messagesByHour)"></span><small>{{ $index % 3 === 0 ? $index : '' }}</small></div>
            }
          </div>
          <p class="muted sm">Best time to run a campaign: around {{ busiestHour(s.messagesByHour) }}:00, when most customers are already chatting (customers are never messaged before 07:00 or after 20:30).</p>
        </section>
      }

      @for (c of campaigns(); track c.id) {
        <section class="card card-pad" [class.on]="c.enabled" [id]="'camp-' + c.type" [class.flash]="flash() === c.type">
          <div class="top">
            <div>
              <h2 class="display sub">{{ c.name }}</h2>
              <p class="muted sm">{{ c.description }}</p>
            </div>
            <label class="switch"><input type="checkbox" [(ngModel)]="c.enabled" [attr.aria-label]="'Turn ' + c.name + ' on or off'" /><span>{{ c.enabled ? 'On' : 'Off' }}</span></label>
          </div>

          <div class="grid">
            <div class="field"><label [for]="'t' + c.id">Send at (Lagos time)</label><input [id]="'t' + c.id" class="input" type="time" [(ngModel)]="c.runTime" /></div>
            <div class="field"><label [for]="'e' + c.id">Repeat every</label>
              <div class="inline"><input [id]="'e' + c.id" class="input num" type="number" min="1" max="60" [(ngModel)]="c.everyNDays" /> <span>day(s)</span></div></div>
            <div class="field"><label [for]="'s' + c.id">From date (optional)</label><input [id]="'s' + c.id" class="input" type="date" [(ngModel)]="c.startDate" /></div>
            <div class="field"><label [for]="'n' + c.id">Until date (optional)</label><input [id]="'n' + c.id" class="input" type="date" [(ngModel)]="c.endDate" /></div>
            <div class="field"><label>Channels</label>
              <div class="inline"><label class="chk"><input type="checkbox" [(ngModel)]="c.sendWhatsApp" /> WhatsApp</label><label class="chk"><input type="checkbox" [(ngModel)]="c.sendEmail" /> Email</label></div></div>
            <div class="field"><label [for]="'h' + c.id">{{ c.forAdmin ? 'Warn me when stock covers fewer than' : '"Running low" means fewer than' }}</label>
              <div class="inline"><input [id]="'h' + c.id" class="input num" type="number" min="1" max="60" [(ngModel)]="c.thresholdDays" /> <span>days of sales</span></div></div>
          </div>

          @if (!c.forAdmin) {
            <details>
              <summary>Safety limits</summary>
              <div class="grid">
                <div class="field"><label [for]="'c' + c.id">Don't message the same customer again for</label><div class="inline"><input [id]="'c' + c.id" class="input num" type="number" min="0" max="60" [(ngModel)]="c.cooldownDays" /> <span>day(s)</span></div></div>
                <div class="field"><label [for]="'m' + c.id">Most customers per run</label><input [id]="'m' + c.id" class="input num" type="number" min="1" max="1000" [(ngModel)]="c.maxRecipients" /></div>
                @if (c.type === 'Reorder') {
                  <div class="field"><label [for]="'f' + c.id">Remind this long after the last purchase, if we don't know their rhythm yet</label><div class="inline"><input [id]="'f' + c.id" class="input num" type="number" min="3" max="120" [(ngModel)]="c.fallbackRepurchaseDays" /> <span>days</span></div></div>
                }
              </div>
            </details>
          }

          @if (c.pending; as p) {
            <div class="notice proposal">
              <strong>Waiting for approval:</strong> {{ c.pendingBy }} proposes {{ p.enabled ? 'ON' : 'OFF' }} at {{ p.runTime }}, every {{ p.everyNDays }} day(s), up to {{ p.maxRecipients }} customers, by {{ p.sendWhatsApp && p.sendEmail ? 'WhatsApp and email' : p.sendWhatsApp ? 'WhatsApp' : 'email' }}.
              @if (auth.isAdmin()) { <span class="inline"><button type="button" class="btn btn-sm btn-primary" [disabled]="busy()" (click)="decide(c, true)">Approve</button><button type="button" class="btn btn-sm" [disabled]="busy()" (click)="decide(c, false)">Reject</button></span> }
            </div>
          }

          <div class="actions">
            <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="save(c)">{{ auth.isMarketer() ? 'Send for approval' : 'Save' }}</button>
            <button type="button" class="btn" [disabled]="busy()" (click)="preview(c)">Show me today's messages</button>
            @if (auth.isAdmin()) { <button type="button" class="btn" [disabled]="busy()" (click)="runNow(c)">Run now</button> }
            @if (c.lastRunNote) { <span class="muted sm">Last run {{ c.lastRunAt ? (c.lastRunAt | stampTime) : '' }}: {{ c.lastRunNote }}</span> }
          </div>

          @if (preview$()?.id === c.id) {
            <div class="sample" aria-live="polite">
              <strong>{{ preview$()!.result.note }}</strong>
              @for (s of preview$()!.result.samples; track $index) { <p class="msg">{{ s }}</p> }
              @empty { <p class="muted sm">Nothing to send right now.</p> }
            </div>
          }
        </section>
      }

      <section class="card card-pad">
        <h2 class="display sub">What was sent</h2>
        <table class="table small">
          <thead><tr><th>When</th><th>Campaign</th><th>To</th><th>Via</th><th>Result</th><th>Message</th></tr></thead>
          <tbody>
            @for (m of log(); track m.id) {
              <tr><td>{{ m.at | stampTime }}</td><td>{{ m.campaign }}</td><td>{{ m.customer }}<br /><span class="muted mono">{{ m.recipient }}</span></td>
                <td>{{ m.channel }}@if (m.mode) { <span class="muted"> ({{ m.mode }})</span> }</td>
                <td><span class="pill" [class.bad]="m.status === 'Failed'" [class.skip]="m.status === 'Skipped'">{{ m.status }}</span>@if (m.detail) { <br /><span class="muted sm">{{ m.detail }}</span> }</td>
                <td class="text">{{ m.text }}</td></tr>
            } @empty { <tr><td colspan="6" class="muted">Nothing sent yet.</td></tr> }
          </tbody>
        </table>
      </section>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 52rem; } .sm { font-size: .8rem; } section { margin-bottom: 1rem; } .sub { font-size: 1.4rem; margin: 0 0 .3rem; }
    .stats { margin: 0 0 1rem; } section.on { border-left: 4px solid var(--brand); }
    .top { display: flex; justify-content: space-between; gap: 1rem; align-items: flex-start; flex-wrap: wrap; }
    .switch { display: inline-flex; align-items: center; gap: .5rem; font-weight: 600; cursor: pointer; } .switch input { width: 1.2rem; height: 1.2rem; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr)); gap: .8rem 1.2rem; margin: .8rem 0; }
    .field label { display: block; font-size: .82rem; margin-bottom: .25rem; } .inline { display: flex; gap: .5rem; align-items: center; flex-wrap: wrap; } .chk { display: inline-flex; gap: .3rem; align-items: center; margin: 0; }
    .num { width: 6rem; } details { margin: .4rem 0; } summary { cursor: pointer; font-weight: 600; }
    .actions { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; margin-top: .8rem; }
    .sample { margin-top: .8rem; padding: .8rem 1rem; background: color-mix(in srgb, var(--brand) 6%, transparent); border-radius: 8px; }
    .msg { margin: .5rem 0 0; white-space: pre-wrap; } .small { font-size: .82rem; } .text { white-space: pre-wrap; max-width: 28rem; }
    .cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr)); gap: 1rem 1.5rem; margin: .6rem 0; } .cols.four { grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); }
    .h3 { font-size: .95rem; margin: .4rem 0; } .r { text-align: right; } .row { display: flex; justify-content: space-between; margin: .15rem 0; font-size: .85rem; border-bottom: 1px dashed var(--line, #ddd); }
    .hours { display: grid; grid-template-columns: repeat(24, 1fr); gap: 2px; height: 5.5rem; align-items: end; margin: .4rem 0; }
    .bar { display: flex; flex-direction: column; justify-content: flex-end; height: 100%; text-align: center; } .bar span { display: block; background: var(--brand); border-radius: 2px 2px 0 0; min-height: 1px; }
    .bar small { font-size: .62rem; color: var(--muted, #777); height: .9rem; } .btn-sm { padding: .15rem .55rem; font-size: .78rem; }
    section.flash { outline: 3px solid var(--brand); }
    .proposal { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: center; margin: .6rem 0; }
    .pill { display: inline-block; padding: .05rem .5rem; border-radius: 99px; background: #dff3e6; font-size: .75rem; } .pill.bad { background: #f8d9d6; } .pill.skip { background: #eee; }
  `,
})
export class CampaignsPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly auth = inject(Auth);
  protected readonly campaigns = signal<Campaign[]>([]);
  protected readonly consent = signal<Consent | null>(null);
  protected readonly log = signal<LogRow[]>([]);
  protected readonly preview$ = signal<{ id: number; result: RunResult } | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly insights = signal<Insights | null>(null);
  protected readonly days = signal(30);
  protected readonly flash = signal('');

  ngOnInit() { void this.load(); void this.loadInsights(); }

  private async loadInsights() {
    try { this.insights.set(await firstValueFrom(this.http.get<Insights>('/api/campaigns/insights', { params: { days: this.days() } }))); }
    catch (e) { this.error.set(messageOf(e)); }
  }
  protected setDays(d: number) { this.days.set(Number(d)); void this.loadInsights(); }
  protected stepName(s: string) { return STEP[s] ?? s; }
  protected dropName(s: string) { return DROP[s] ?? s; }
  protected langName(s: string) { return LANG[s] ?? s; }
  protected pct(n: number, of: number) { return of > 0 ? `(${Math.round((100 * n) / of)}%)` : ''; }
  protected barHeight(n: number, all: number[]) { const max = Math.max(...all, 1); return Math.round((100 * n) / max); }
  protected busiestHour(all: number[]) { let best = 10; all.forEach((n, h) => { if (h >= 7 && h <= 20 && n > all[best]) best = h; }); return String(best).padStart(2, '0'); }
  protected hoursLabel(all: number[]) { return `Customer messages by hour; busiest ${this.busiestHour(all)}:00`; }

  /** "Follow up": jump to the campaign that messages these people and show who it would pick today. */
  protected target(type: string) {
    const c = this.campaigns().find(x => x.type === type);
    if (!c) return;
    this.flash.set(type);
    document.getElementById('camp-' + type)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    void this.preview(c);
  }

  private async load() {
    try {
      this.campaigns.set(await firstValueFrom(this.http.get<Campaign[]>('/api/campaigns')));
      this.consent.set(await firstValueFrom(this.http.get<Consent>('/api/campaigns/consent')));
      this.log.set(await firstValueFrom(this.http.get<LogRow[]>('/api/campaigns/log', { params: { take: 100 } })));
    } catch (e) { this.error.set(messageOf(e)); }
  }

  private body(c: Campaign) {
    return { enabled: c.enabled, sendWhatsApp: c.sendWhatsApp, sendEmail: c.sendEmail, runTime: c.runTime, everyNDays: Number(c.everyNDays), startDate: c.startDate || null, endDate: c.endDate || null,
      cooldownDays: Number(c.cooldownDays), maxRecipients: Number(c.maxRecipients), thresholdDays: Number(c.thresholdDays), fallbackRepurchaseDays: Number(c.fallbackRepurchaseDays) };
  }

  protected async save(c: Campaign) {
    this.busy.set(true); this.error.set('');
    try {
      const saved = await firstValueFrom(this.http.put<Campaign>(`/api/campaigns/${c.id}`, this.body(c)));
      this.campaigns.update(l => l.map(x => x.id === saved.id ? saved : x));
      this.toasts.ok(this.auth.isMarketer() ? `Sent to an administrator for approval. ${saved.name} changes once they approve.` : `${saved.name} is ${saved.enabled ? `on — next run ${saved.runTime}` : 'off'}.`);
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  /** An admin approves or rejects a digital marketer's proposed settings. */
  protected async decide(c: Campaign, approve: boolean) {
    this.busy.set(true); this.error.set('');
    try {
      const saved = await firstValueFrom(this.http.post<Campaign>(`/api/campaigns/${c.id}/${approve ? 'approve' : 'reject'}-change`, {}));
      this.campaigns.update(l => l.map(x => x.id === saved.id ? saved : x));
      this.toasts.ok(approve ? `Approved: ${saved.name} is ${saved.enabled ? 'on' : 'off'}.` : 'Proposal rejected.');
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async preview(c: Campaign) {
    this.busy.set(true); this.error.set('');
    try { this.preview$.set({ id: c.id, result: await firstValueFrom(this.http.post<RunResult>(`/api/campaigns/${c.id}/preview`, {})) }); }
    catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async runNow(c: Campaign) {
    const ok = await this.confirm.ask({
      title: `Run "${c.name}" now?`, confirmLabel: 'Run now',
      message: 'Real messages go out to the customers it picks, using the same rules as the scheduled run (opted in, cooldown, quiet hours). Use "Show me today\'s messages" first to see who and what.',
    });
    if (ok === null) return;
    this.busy.set(true); this.error.set('');
    try { const r = await firstValueFrom(this.http.post<RunResult>(`/api/campaigns/${c.id}/run`, {})); this.toasts.ok(r.note); await this.load(); }
    catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
