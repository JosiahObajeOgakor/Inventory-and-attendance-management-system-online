import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { Icon } from '../../shared/icon';
import { NairaPipe } from '../../shared/ui';

interface PartnerDoc { id: number; kind: string; fileName: string; contentType: string; size: number; uploadedAt: string; }
interface Partner {
  id: number; businessName: string; contactName: string; phone: string; email: string; address: string; cacNumber: string; taxId: string | null;
  description: string; requestedTier: string; approvedTier: string | null; status: 'Pending' | 'Approved' | 'Rejected' | 'Suspended'; channel: string;
  verification: string; verifiedName: string | null; verificationNote: string | null; customerId: number | null; createdAt: string; decidedAt: string | null;
  decidedBy: string | null; decisionNote: string | null; walletBalance: number; documents: PartnerDoc[];
}

const STATUSES = ['Pending', 'Approved', 'Suspended', 'Rejected'] as const;

/** Pet shops, wholesalers and distributors asking for trade prices: check their CAC, look at the certificate, then approve at a tier, reject, suspend or delete. */
@Component({
  selector: 'app-partners',
  imports: [Icon, NairaPipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Trade partners</h1></div>
      <p class="muted lede">Businesses that applied to buy at wholesale or distributor prices. Nobody sees trade prices until you approve them here; the chat and the shop quote retail to everyone else.</p>

      <div class="tabs" role="tablist" aria-label="Filter by status">
        @for (s of statuses; track s) {
          <button type="button" role="tab" class="tab" [class.on]="status() === s" [attr.aria-selected]="status() === s" (click)="setStatus(s)">
            {{ s }} <span class="count">{{ counts()[s] ?? 0 }}</span></button>
        }
      </div>

      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      @if (loading() && !all().length) { <p class="muted">Loading…</p> }

      <div class="list">
        @for (p of shown(); track p.id) {
          <article class="card partner" [class.open]="openId() === p.id">
            <button type="button" class="head" (click)="toggle(p.id)" [attr.aria-expanded]="openId() === p.id">
              <span class="avatar" aria-hidden="true">{{ initials(p.businessName) }}</span>
              <span class="who"><span class="strong">{{ p.businessName }}</span>
                <span class="muted sm">{{ p.contactName }} · {{ pretty(p.phone) }} · {{ p.createdAt | date: 'd MMM y' }}</span></span>
              <span class="chips">
                <span class="chip">{{ p.status === 'Approved' || p.status === 'Suspended' ? p.approvedTier : 'Wants ' + p.requestedTier }}</span>
                <span class="chip" [class]="'v-' + p.verification">{{ verificationLabel(p) }}</span>
              </span>
              <app-icon name="chevron" [size]="18" class="caret" />
            </button>

            @if (openId() === p.id) {
              <div class="body">
                <dl class="facts">
                  <div><dt>Email</dt><dd><a [href]="'mailto:' + p.email">{{ p.email }}</a></dd></div>
                  <div><dt>Phone</dt><dd><a [href]="'https://wa.me/' + p.phone" target="_blank" rel="noopener">{{ pretty(p.phone) }}</a></dd></div>
                  <div><dt>Address</dt><dd>{{ p.address }}</dd></div>
                  <div><dt>CAC number</dt><dd class="mono">{{ p.cacNumber }}</dd></div>
                  <div><dt>TIN</dt><dd class="mono">{{ p.taxId || '—' }}</dd></div>
                  <div><dt>Applied via</dt><dd>{{ p.channel }}</dd></div>
                  @if (p.status === 'Approved' || p.status === 'Suspended') { <div><dt>Wallet</dt><dd class="mono">{{ p.walletBalance | naira }}</dd></div> }
                </dl>
                <div class="verify" [class]="'v-' + p.verification">
                  <strong>{{ verificationLabel(p) }}</strong>
                  @if (p.verifiedName) { <span>Registry name: <b>{{ p.verifiedName }}</b></span> }
                  @if (p.verificationNote) { <span class="muted">{{ p.verificationNote }}</span> }
                  <button type="button" class="btn btn-sm" (click)="act(p, 'reverify')" [disabled]="busy()">Re-check</button>
                </div>
                <blockquote class="about">{{ p.description }}</blockquote>
                @if (p.documents.length) {
                  <div class="docs">
                    @for (d of p.documents; track d.id) {
                      <button type="button" class="btn btn-sm" (click)="openDoc(p, d)"><app-icon name="download" [size]="15" /> {{ d.kind === 'cac' ? 'CAC certificate' : d.kind === 'id' ? 'ID' : 'Other' }} · {{ kb(d.size) }}</button>
                    }
                  </div>
                } @else { <p class="muted sm">No documents attached (applied on WhatsApp). Ask for the CAC certificate before approving.</p> }
                @if (p.decidedBy) { <p class="muted sm">{{ p.status }} by {{ p.decidedBy }} {{ p.decidedAt | date: 'd MMM y, h:mm a' }}{{ p.decisionNote ? ' — ' + p.decisionNote : '' }}</p> }

                <div class="msgs">
                  <h3 class="sub">Messages @if (thread().length) { <span class="muted sm">({{ thread().length }})</span> }</h3>
                  <div class="thread">
                    @for (m of thread(); track m.id) { <div class="bubble" [class.me]="!m.fromPartner"><p>{{ m.text }}</p><small>{{ m.fromPartner ? p.contactName : m.by }} · {{ m.at | date: 'd MMM, h:mm a' }}</small></div> }
                    @empty { <p class="muted sm">No messages yet. Partners write from their account on the website.</p> }
                  </div>
                  <div class="reply"><textarea class="input" rows="2" maxlength="2000" placeholder="Reply to {{ p.businessName }}…" [value]="reply()" (input)="reply.set($any($event.target).value)"></textarea>
                    <button type="button" class="btn btn-primary" [disabled]="busy() || !reply().trim()" (click)="send(p)">Send</button></div>
                </div>

                <div class="decide">
                  <label class="field grow"><span>Note to the applicant (optional)</span>
                    <input class="input" [value]="note()" (input)="note.set($any($event.target).value)" maxlength="300" placeholder="e.g. Approved for 3 months trial" /></label>
                  <div class="actions">
                    @switch (p.status) {
                      @case ('Pending') {
                        <button type="button" class="btn btn-primary" (click)="approve(p, 'Wholesaler')" [disabled]="busy()">Approve as wholesaler</button>
                        <button type="button" class="btn btn-primary" (click)="approve(p, 'Distributor')" [disabled]="busy()">Approve as distributor</button>
                        <button type="button" class="btn" (click)="act(p, 'reject')" [disabled]="busy()">Reject</button>
                      }
                      @case ('Approved') {
                        <button type="button" class="btn" (click)="approve(p, p.approvedTier === 'Wholesaler' ? 'Distributor' : 'Wholesaler')" [disabled]="busy()">Change to {{ p.approvedTier === 'Wholesaler' ? 'distributor' : 'wholesaler' }}</button>
                        <button type="button" class="btn btn-danger" (click)="act(p, 'suspend')" [disabled]="busy()">Suspend</button>
                      }
                      @case ('Suspended') { <button type="button" class="btn btn-primary" (click)="act(p, 'reinstate')" [disabled]="busy()">Reinstate</button> }
                      @case ('Rejected') {
                        <button type="button" class="btn btn-primary" (click)="approve(p, p.requestedTier)" [disabled]="busy()">Approve as {{ p.requestedTier.toLowerCase() }} after all</button>
                      }
                    }
                    <button type="button" class="btn btn-ghost danger" (click)="remove(p)" [disabled]="busy()">Delete</button>
                  </div>
                </div>
              </div>
            }
          </article>
        } @empty {
          @if (!loading()) { <div class="card empty"><strong>No {{ status().toLowerCase() }} partners</strong>{{ status() === 'Pending' ? 'New applications from the website and WhatsApp land here, and you are told on WhatsApp, SMS and email.' : '' }}</div> }
        }
      </div>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 60rem; } .sm { font-size: .8rem; }
    .tabs { display: flex; gap: .25rem; flex-wrap: wrap; margin-bottom: 1rem; border-bottom: 1px solid var(--border, #e3e5ea); }
    .tab { background: none; border: 0; padding: .6rem .9rem; font: inherit; cursor: pointer; border-bottom: 2px solid transparent; color: var(--muted, #5b6170); }
    .tab.on { color: var(--text, #1c2130); border-bottom-color: var(--primary, #a3480a); font-weight: 600; }
    .count { display: inline-block; min-width: 1.4rem; padding: 0 .35rem; border-radius: 1rem; background: var(--surface-2, #f1f2f5); font-size: .75rem; text-align: center; }
    .list { display: grid; gap: .75rem; }
    .partner { padding: 0; overflow: hidden; }
    .head { width: 100%; display: flex; gap: .85rem; align-items: center; padding: .9rem 1rem; background: none; border: 0; font: inherit; text-align: left; cursor: pointer; color: inherit; }
    .avatar { flex: none; width: 2.6rem; height: 2.6rem; border-radius: .7rem; display: grid; place-items: center; font-weight: 700; color: #fff;
      background: linear-gradient(135deg, #a3480a, #d9822b); letter-spacing: .02em; }
    .caret { transform: rotate(90deg); transition: transform .15s; } .open .caret { transform: rotate(-90deg); }
    .who { display: grid; flex: 1; min-width: 0; } .who .muted { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .chips { display: flex; gap: .4rem; flex-wrap: wrap; justify-content: flex-end; }
    .chip { font-size: .75rem; padding: .2rem .55rem; border-radius: 1rem; background: var(--surface-2, #f1f2f5); white-space: nowrap; }
    .v-Verified { color: #146c2e; } .chip.v-Verified { background: #e3f4e8; }
    .v-Mismatch, .v-NotFound { color: #9a3412; } .chip.v-Mismatch, .chip.v-NotFound { background: #fdecd8; }
    .v-Failed { color: #b42318; } .chip.v-Failed { background: #fde4e1; }
    .body { padding: 0 1rem 1rem; border-top: 1px solid var(--border, #e3e5ea); }
    .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: .6rem 1.25rem; margin: 1rem 0; }
    .facts dt { font-size: .75rem; color: var(--muted, #5b6170); text-transform: uppercase; letter-spacing: .04em; } .facts dd { margin: .1rem 0 0; overflow-wrap: anywhere; }
    .verify { display: flex; flex-wrap: wrap; gap: .4rem 1rem; align-items: center; padding: .7rem .9rem; border-radius: .6rem; background: var(--surface-2, #f6f7f9); }
    .verify .btn { margin-left: auto; }
    .about { margin: 1rem 0; padding: .2rem 0 .2rem .9rem; border-left: 3px solid var(--primary, #a3480a); color: var(--text, #1c2130); white-space: pre-line; }
    .docs { display: flex; flex-wrap: wrap; gap: .5rem; margin-bottom: .75rem; }
    .decide { display: grid; gap: .75rem; margin-top: 1rem; }
    .msgs { margin-top: 1rem; display: grid; gap: .6rem; } .sub { font-size: .95rem; margin: 0; }
    .thread { display: flex; flex-direction: column; gap: .45rem; max-height: 18rem; overflow-y: auto; padding: .5rem; border-radius: 10px; background: var(--surface-2, #f6f7f9); }
    .bubble { max-width: 80%; align-self: flex-start; padding: .5rem .75rem; border-radius: 12px; background: #fff; border: 1px solid var(--border, #e3e5ea); }
    .bubble.me { align-self: flex-end; background: color-mix(in srgb, var(--primary, #a3480a) 12%, #fff); }
    .bubble p { margin: 0; white-space: pre-wrap; } .bubble small { color: var(--muted, #5b6170); font-size: .72rem; }
    .reply { display: flex; gap: .5rem; align-items: flex-end; } .reply textarea { flex: 1; }
    .field { display: grid; gap: .3rem; } .field span { font-size: .8rem; color: var(--muted, #5b6170); }
    .actions { display: flex; flex-wrap: wrap; gap: .5rem; } .danger { color: var(--danger, #b42318); }
    .empty { display: grid; gap: .3rem; padding: 2rem; text-align: center; color: var(--muted, #5b6170); }
    @media (max-width: 640px) {
      .head { flex-wrap: wrap; } .chips { order: 3; width: 100%; justify-content: flex-start; padding-left: 3.45rem; }
      .actions .btn { flex: 1 1 100%; } .verify .btn { margin-left: 0; }
    }
  `,
})
export class PartnersPage implements OnInit {
  private readonly http = inject(HttpClient);
  protected readonly statuses = STATUSES;
  protected readonly status = signal<string>('Pending');
  protected readonly all = signal<Partner[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly openId = signal<number | null>(null);
  protected readonly note = signal('');
  protected readonly thread = signal<{ id: number; fromPartner: boolean; text: string; by: string | null; at: string }[]>([]);
  protected readonly reply = signal('');
  protected readonly shown = computed(() => this.all().filter(p => p.status === this.status()));
  protected readonly counts = computed(() => this.all().reduce<Partial<Record<string, number>>>((m, p) => (m[p.status] = (m[p.status] ?? 0) + 1, m), {}));

  ngOnInit() { void this.load(); }

  private async load() {
    this.loading.set(true);
    try { this.all.set(await firstValueFrom(this.http.get<Partner[]>('/api/trade-partners'))); this.error.set(''); }
    catch (e) { this.error.set(this.message(e)); }
    finally { this.loading.set(false); }
  }

  protected setStatus(s: string) { this.status.set(s); this.openId.set(null); }
  protected toggle(id: number) {
    this.openId.set(this.openId() === id ? null : id); this.note.set(''); this.reply.set(''); this.thread.set([]);
    if (this.openId() === id) void firstValueFrom(this.http.get<{ id: number; fromPartner: boolean; text: string; by: string | null; at: string }[]>(`/api/trade-partners/${id}/messages`)).then(t => this.thread.set(t), () => {});
  }

  protected async send(p: Partner) {
    this.busy.set(true);
    try { this.thread.set(await firstValueFrom(this.http.post<{ id: number; fromPartner: boolean; text: string; by: string | null; at: string }[]>(`/api/trade-partners/${p.id}/messages`, { text: this.reply() }))); this.reply.set(''); this.error.set(''); }
    catch (e) { this.error.set(this.message(e)); }
    finally { this.busy.set(false); }
  }

  protected approve(p: Partner, tier: string) { return this.run(p, 'approve', { tier, note: this.note() || null }); }
  protected act(p: Partner, action: 'reject' | 'suspend' | 'reinstate' | 'reverify') {
    if (action === 'suspend' && !confirm(`Suspend ${p.businessName}? They go back to retail prices and lose portal access at once.`)) return;
    return this.run(p, action, action === 'reverify' ? {} : { note: this.note() || null });
  }

  protected async remove(p: Partner) {
    if (!confirm(`Delete ${p.businessName}'s application and documents? This can't be undone. (A partner with wallet history can only be suspended.)`)) return;
    this.busy.set(true);
    try { await firstValueFrom(this.http.delete(`/api/trade-partners/${p.id}`)); await this.load(); this.error.set(''); }
    catch (e) { this.error.set(this.message(e)); }
    finally { this.busy.set(false); }
  }

  private async run(p: Partner, action: string, body: object) {
    this.busy.set(true);
    try { await firstValueFrom(this.http.post(`/api/trade-partners/${p.id}/${action}`, body)); this.note.set(''); await this.load(); this.error.set(''); }
    catch (e) { this.error.set(this.message(e)); }
    finally { this.busy.set(false); }
  }

  /** Documents come through the API (with the login), then open in a new tab. */
  protected async openDoc(p: Partner, d: PartnerDoc) {
    const tab = window.open('', '_blank');
    try {
      const blob = await firstValueFrom(this.http.get(`/api/trade-partners/${p.id}/documents/${d.id}`, { responseType: 'blob' }));
      const url = URL.createObjectURL(blob);
      if (tab) tab.location.href = url; else window.location.href = url;
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) { tab?.close(); this.error.set(this.message(e)); }
  }

  protected verificationLabel(p: Partner) {
    return ({ Verified: 'CAC verified', Mismatch: 'CAC name differs', NotFound: 'CAC not found', Failed: 'Check failed', NotChecked: 'Check certificate' } as Record<string, string>)[p.verification] ?? p.verification;
  }
  protected initials(name: string) { return name.split(/\s+/).filter(w => /[a-z0-9]/i.test(w)).slice(0, 2).map(w => w[0].toUpperCase()).join(''); }
  protected pretty(phone: string) { return phone.startsWith('234') && phone.length === 13 ? `0${phone.slice(3, 6)} ${phone.slice(6, 9)} ${phone.slice(9)}` : '+' + phone; }
  protected kb(bytes: number) { return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`; }
  private message(e: unknown) { return e instanceof HttpErrorResponse ? (e.error?.detail ?? e.error?.title ?? 'Something went wrong. Please try again.') : 'Something went wrong. Please try again.'; }
}
