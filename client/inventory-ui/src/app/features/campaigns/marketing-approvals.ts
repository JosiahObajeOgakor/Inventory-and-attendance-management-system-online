import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { StampTimePipe } from '../../shared/ui';
import { Approval, EngineApi } from '../apps-engine/engine-api';

interface PendingCampaign { id: number; name: string; pendingBy: string | null; pendingAt: string | null;
  pending: { enabled: boolean; runTime: string; everyNDays: number; maxRecipients: number; sendWhatsApp: boolean; sendEmail: boolean } | null; }

/**
 * What the digital marketers want to put in front of customers: broadcasts, automation rules, social and blog posts, surveys and campaign settings.
 * The admin reads exactly what would go out, then approves (it goes live at once, in the admin's name) or rejects with a note the marketer sees.
 */
@Component({
  selector: 'app-marketing-approvals',
  imports: [RouterLink, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Marketing approvals</h1></div>
      <p class="muted lede">Your digital marketers can prepare anything, but nothing reaches customers until you approve it here. You're told on WhatsApp and email when something is waiting.</p>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      @if (loading()) { <p class="muted">Loading…</p> }

      @if (!loading() && !count()) { <div class="card empty"><strong>Nothing waiting</strong>When a marketer sends something for approval it appears here.</div> }

      <div class="list">
        @for (c of campaigns(); track c.id) {
          <article class="card item">
            <header><span class="chip">Campaign settings</span><h2>{{ c.name }}</h2><span class="muted sm">from {{ c.pendingBy }} · {{ c.pendingAt | stampTime }}</span></header>
            @if (c.pending; as p) {
              <p>Turn it <strong>{{ p.enabled ? 'ON' : 'OFF' }}</strong> at {{ p.runTime }}, every {{ p.everyNDays }} day(s), up to {{ p.maxRecipients }} customers per run, by {{ p.sendWhatsApp && p.sendEmail ? 'WhatsApp and email' : p.sendWhatsApp ? 'WhatsApp' : 'email' }}.</p>
            }
            <div class="actions">
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="campaign(c, true)">Approve</button>
              <button type="button" class="btn" [disabled]="busy()" (click)="campaign(c, false)">Reject</button>
              <a class="btn btn-ghost" routerLink="/campaigns">Open Campaigns</a>
            </div>
          </article>
        }
        @for (a of items(); track a.app + a.id) {
          <article class="card item">
            <header><span class="chip">{{ a.appName }}</span><h2>{{ a.title }}</h2><span class="muted sm">{{ a.submittedBy }} wants to <strong>{{ a.what }}</strong> · {{ a.submittedAt | stampTime }}</span></header>
            <dl class="content">
              @for (f of a.content; track f.label) { <div [class.wide]="f.value.length > 80"><dt>{{ f.label }}</dt><dd>{{ f.value }}</dd></div> }
            </dl>
            <div class="actions">
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="approve(a)">Approve</button>
              <button type="button" class="btn" [disabled]="busy()" (click)="reject(a)">Reject…</button>
              <a class="btn btn-ghost" [routerLink]="['/apps', a.app]">Open {{ a.appName }}</a>
            </div>
          </article>
        }
      </div>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 52rem; } .sm { font-size: .8rem; }
    .list { display: grid; gap: .9rem; } .item { padding: 1rem 1.1rem; }
    header { display: flex; flex-wrap: wrap; gap: .3rem .8rem; align-items: baseline; margin-bottom: .6rem; } header h2 { font-size: 1.1rem; margin: 0; }
    .chip { font-size: .72rem; padding: .15rem .55rem; border-radius: 1rem; background: var(--surface-2, #f1f2f5); font-weight: 600; }
    .content { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: .6rem 1.2rem; margin: 0 0 .8rem; }
    .content .wide { grid-column: 1 / -1; } dt { font-size: .74rem; text-transform: uppercase; letter-spacing: .04em; color: var(--muted, #5b6170); }
    dd { margin: .15rem 0 0; white-space: pre-wrap; overflow-wrap: anywhere; }
    .actions { display: flex; flex-wrap: wrap; gap: .5rem; }
    .empty { display: grid; gap: .3rem; padding: 2rem; text-align: center; color: var(--muted, #5b6170); }
    @media (max-width: 600px) { .actions .btn { flex: 1 1 100%; } }
  `,
})
export class MarketingApprovalsPage implements OnInit {
  private readonly api = inject(EngineApi);
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly items = signal<Approval[]>([]);
  protected readonly campaigns = signal<PendingCampaign[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly count = computed(() => this.items().length + this.campaigns().length);

  ngOnInit() { void this.load(); }

  private async load() {
    this.loading.set(true);
    try {
      const [items, camps] = await Promise.all([this.api.approvals(), firstValueFrom(this.http.get<PendingCampaign[]>('/api/campaigns'))]);
      this.items.set(items); this.campaigns.set(camps.filter(c => c.pending)); this.error.set('');
    } catch (e) { this.error.set(messageOf(e)); } finally { this.loading.set(false); }
  }

  protected async approve(a: Approval) {
    if ((await this.confirm.ask({ title: `Approve "${a.title}"?`, message: `It will ${a.what} straight away, in your name.`, confirmLabel: 'Approve' })) === null) return;
    await this.run(async () => { const r = await this.api.approve(a.app, a.id); this.toasts.ok(r.message ?? 'Approved.'); });
  }

  protected async reject(a: Approval) {
    const note = await this.confirm.ask({ title: `Reject "${a.title}"?`, message: 'The marketer sees your note and can change it and try again.', confirmLabel: 'Reject', reason: { label: 'What should change?', required: true } });
    if (note === null) return;
    await this.run(async () => { await this.api.reject(a.app, a.id, note); this.toasts.ok('Rejected. The marketer can see your note.'); });
  }

  protected async campaign(c: PendingCampaign, approve: boolean) {
    await this.run(async () => {
      await firstValueFrom(this.http.post(`/api/campaigns/${c.id}/${approve ? 'approve' : 'reject'}-change`, {}));
      this.toasts.ok(approve ? `Approved: ${c.name} updated.` : 'Proposal rejected.');
    });
  }

  private async run(fn: () => Promise<void>) {
    this.busy.set(true);
    try { await fn(); await this.load(); } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
