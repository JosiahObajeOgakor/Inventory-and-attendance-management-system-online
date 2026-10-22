import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Auth } from '../../core/auth.service';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';
import { NairaPipe, StampTimePipe } from '../../shared/ui';
import { Approval, ApprovalRule, ErpApi } from './erp-api';

const ACTIONS: Record<string, string> = { 'stock.adjust': 'Stock write-off / adjustment', 'sales.void': 'Voiding a sale' };

/** The approvals inbox: actions held because they were worth more than the limit, decided by someone other than the person who asked. */
@Component({
  selector: 'app-approvals',
  imports: [FormsModule, NairaPipe, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Approvals</h1><span class="muted">Big write-offs and voids wait here until a second person agrees</span></div>
      <div class="tabs" role="tablist">
        @if (auth.can('approvals.decide')) {
          <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'inbox'" (click)="show('inbox')">Waiting for me</button>
          <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'history'" (click)="show('history')">Decided</button>
        }
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'mine'" (click)="show('mine')">My requests</button>
        @if (auth.can('approvals.decide')) { <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'rules'" (click)="show('rules')">Limits</button> }
      </div>

      @if (tab() === 'rules') {
        <section class="card pad">
          <p class="muted">An action worth more than its limit is held for approval instead of happening straight away.</p>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Action</th><th>On</th><th class="num">Limit (₦)</th><th></th></tr></thead>
            <tbody>@for (r of rules(); track r.action) {
              <tr><td class="strong">{{ name(r.action) }}</td>
                <td><input type="checkbox" [(ngModel)]="r.enabled" [disabled]="!auth.can('company.edit')" [attr.aria-label]="'Require approval for ' + name(r.action)" /></td>
                <td class="num"><input class="input num" type="number" min="0" [(ngModel)]="r.thresholdAmount" [disabled]="!auth.can('company.edit')" aria-label="Limit" /></td>
                <td>@if (auth.can('company.edit')) { <button type="button" class="btn btn-sm" (click)="saveRule(r)">Save</button> }</td></tr>
            }</tbody>
          </table></div>
        </section>
      } @else {
        <section class="card">
          <div class="list">
            @for (a of items(); track a.id) {
              <article class="item">
                <div class="main">
                  <div class="top"><span class="strong">{{ name(a.action) }}</span><span class="mono amt">{{ a.amount | naira }}</span>
                    <span class="stamp" [class]="'stamp stamp-' + kind(a.status)">{{ a.status }}</span></div>
                  <p>{{ a.summary }}</p>
                  <p class="muted sm">Asked by {{ a.requestedBy }} · {{ a.requestedAt | stampTime }}@if (a.decidedBy) { · {{ a.status === 'Rejected' ? 'rejected' : 'approved' }} by {{ a.decidedBy }} · {{ a.decidedAt | stampTime }} }
                    @if (a.decisionNote) { · “{{ a.decisionNote }}” }@if (a.failureReason) { · <span class="bad">{{ a.failureReason }}</span> }</p>
                </div>
                @if (tab() === 'inbox' && a.status === 'Pending') {
                  <div class="acts">
                    <button type="button" class="btn btn-danger" (click)="decide(a, false)">Reject</button>
                    <button type="button" class="btn btn-primary" (click)="decide(a, true)">Approve</button>
                  </div>
                }
              </article>
            }
            @if (!items().length) { <div class="empty"><strong>{{ tab() === 'inbox' ? 'Nothing waiting' : 'Nothing here yet' }}</strong></div> }
          </div>
        </section>
      }
    </div>`,
  styles: `.pad { padding: 1rem; } .sm { font-size: .78rem; } .list { display: grid; } .bad { color: var(--bad, #b3261e); }
    .item { display: flex; gap: 1rem; align-items: center; padding: .9rem 1rem; border-bottom: 1px solid var(--line); } .main { flex: 1; } .main p { margin: .2rem 0 0; }
    .top { display: flex; gap: .7rem; align-items: center; flex-wrap: wrap; } .amt { font-weight: 700; } .acts { display: flex; gap: .5rem; }
    @media (max-width: 640px) { .item { flex-direction: column; align-items: stretch; } }`,
})
export class ApprovalsPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(ErpApi);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly tab = signal<'inbox' | 'history' | 'mine' | 'rules'>('inbox');
  protected readonly items = signal<Approval[]>([]);
  protected readonly rules = signal<ApprovalRule[]>([]);

  ngOnInit() { this.show(this.auth.can('approvals.decide') ? 'inbox' : 'mine'); }
  protected name(a: string) { return ACTIONS[a] ?? a; }
  protected kind(s: string) { return s === 'Pending' ? 'warn' : s === 'Executed' ? 'ok' : s === 'Rejected' || s === 'Failed' ? 'bad' : 'info'; }

  protected async show(t: 'inbox' | 'history' | 'mine' | 'rules') {
    this.tab.set(t);
    try {
      if (t === 'inbox') this.items.set(await this.api.approvals('Pending'));
      if (t === 'history') this.items.set([...await this.api.approvals('Executed'), ...await this.api.approvals('Rejected'), ...await this.api.approvals('Failed')]
        .sort((a, b) => (b.decidedAt ?? '').localeCompare(a.decidedAt ?? '')));
      if (t === 'mine') this.items.set(await this.api.myApprovals());
      if (t === 'rules') this.rules.set(await this.api.approvalRules());
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async decide(a: Approval, approve: boolean) {
    const note = await this.confirm.ask({ title: `${approve ? 'Approve' : 'Reject'}: ${this.name(a.action)}?`, message: approve ? `${a.summary}. It is carried out as soon as you approve.` : a.summary,
      confirmLabel: approve ? 'Approve' : 'Reject', danger: !approve, reason: { label: approve ? 'Note (optional)' : 'Why are you rejecting it?', required: !approve } });
    if (note === null) return;
    try {
      const r = await this.api.decide(a.id, approve, note || null);
      if (r.status === 'Failed') this.toasts.error(`Approved, but it could not be carried out: ${r.failureReason}`);
      else this.toasts.ok(approve ? 'Approved and carried out.' : 'Rejected.');
      await this.show('inbox');
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async saveRule(r: ApprovalRule) {
    try { await this.api.setApprovalRule({ ...r, thresholdAmount: Number(r.thresholdAmount) || 0 }); this.toasts.ok('Limit saved.'); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
