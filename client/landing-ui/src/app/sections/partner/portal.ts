import { Component, ElementRef, PLATFORM_ID, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { whatsappHref } from '../../core/whatsapp';
import { PortalState } from './portal-state';

interface Price { id: number; name: string; unit: string; price: number; retail: number; inStock: boolean; }
interface OrderRow { number: string; date: string; total: number; paid: number; status: string; }
interface Account { store: string; storeName: string; businessName: string; contactName: string; status: string; tier: string | null; requestedTier: string; note: string | null;
  appliedAt: string; owed: number; wallet: number; prices: Price[]; orders: OrderRow[]; }
interface WalletRow { kind: string; amount: number; status: string; reference: string; method: string; at: string; note: string | null; }
interface Message { id: number; fromPartner: boolean; text: string; by: string | null; at: string; }
interface Notice { kind: string; text: string; at: string; }
type Tab = 'shop' | 'wallet' | 'orders' | 'messages';

const SESSION = 'partner-session', TRUSTED = 'trusted-browser';
const TOP_UPS = [50_000, 100_000, 250_000, 500_000];

/**
 * The trade partner's own account: their prices next to retail with a basket and checkout (wallet or AlatPay), the wallet with top-ups through AlatPay,
 * orders, messages to the business, and notifications. Sign-in is by email: a browser that signed in before goes straight in; a new one gets a code.
 */
@Component({
  selector: 'app-partner-portal',
  templateUrl: './portal.html',
  styleUrl: './portal.scss',
})
export class PartnerPortal {
  private readonly http = inject(HttpClient);
  protected readonly state = inject(PortalState);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('code');
  private readonly thread = viewChild<ElementRef<HTMLElement>>('thread');

  protected readonly phase = signal<'email' | 'code' | 'loading' | 'in'>('email');
  protected readonly email = signal('');
  protected readonly info = signal('');
  protected readonly error = signal('');
  protected readonly busy = signal(false);
  protected readonly toast = signal('');

  protected readonly accounts = signal<Account[]>([]);
  protected readonly maskedEmail = signal('');
  protected readonly storeIndex = signal(0);
  protected readonly account = computed(() => this.accounts()[this.storeIndex()] ?? null);
  protected readonly approved = computed(() => this.account()?.status === 'Approved');
  protected readonly tab = signal<Tab>('shop');

  protected readonly query = signal('');
  protected readonly basket = signal<Record<number, number>>({});
  protected readonly checkoutOpen = signal(false);
  protected readonly payWith = signal<'wallet' | 'online'>('wallet');
  protected readonly note = signal('');

  protected readonly wallet = signal<WalletRow[]>([]);
  protected readonly topUp = signal<number | null>(TOP_UPS[1]);
  protected readonly topUps = TOP_UPS;
  protected readonly messages = signal<Message[]>([]);
  protected readonly draft = signal('');
  protected readonly notices = signal<Notice[]>([]);
  protected readonly bellOpen = signal(false);
  protected readonly seenNotices = signal(0);

  protected readonly shown = computed(() => {
    const q = this.query().trim().toLowerCase();
    return (this.account()?.prices ?? []).filter(p => !q || p.name.toLowerCase().includes(q));
  });
  protected readonly lines = computed(() => {
    const b = this.basket(); const prices = this.account()?.prices ?? [];
    return Object.entries(b).map(([id, qty]) => ({ p: prices.find(x => x.id === +id)!, qty })).filter(l => l.p && l.qty > 0);
  });
  protected readonly count = computed(() => this.lines().reduce((n, l) => n + l.qty, 0));
  protected readonly subtotal = computed(() => this.lines().reduce((n, l) => n + l.qty * l.p.price, 0));
  protected readonly saving = computed(() => this.lines().reduce((n, l) => n + l.qty * Math.max(0, l.p.retail - l.p.price), 0));
  protected readonly unread = computed(() => Math.max(0, this.notices().length - this.seenNotices()));
  protected readonly whatsapp = computed(() => whatsappHref(`Hello, this is ${this.account()?.businessName ?? 'a trade partner'}.`));

  constructor() {
    if (!this.browser) return;
    // #partner opens the portal; coming back from AlatPay (?partner=paid) opens it and asks AlatPay straight away
    const fromHash = () => { if (location.hash === '#partner') this.state.open(); };
    fromHash(); addEventListener('hashchange', fromHash);
    if (new URLSearchParams(location.search).get('partner') === 'paid') { this.state.open(); this.returning = true; }
    effect(() => {
      if (!this.state.isOpen()) return;
      untracked(() => void this.opened());
    });
  }
  private returning = false;

  private async opened(): Promise<void> {
    document.documentElement.style.overflow = 'hidden';
    const h = this.state.handover();
    if (h) {
      this.state.handover.set(null); this.email.set(h.email);
      if (h.next === 'signedin' && h.session) { write(SESSION, h.session); await this.enter(); return; }
      this.phase.set('code'); this.info.set(`We've emailed a 6-digit code to ${h.email}.`); this.focusCode(); return;
    }
    if (read(SESSION)) { this.phase.set('loading'); if (await this.enter()) return; }
    this.phase.set('email');
  }

  protected close(): void {
    this.state.close(); document.documentElement.style.overflow = '';
    if (location.hash === '#partner') history.replaceState(null, '', location.pathname + location.search);
  }

  // ------------------------------------------------------------------ sign in

  protected async start(form: HTMLFormElement): Promise<void> {
    if (!form.reportValidity()) return;
    const email = String(new FormData(form).get('email') ?? '').trim();
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ next: string; session?: string; message?: string }>('/api/trade/start', { email }, { headers: this.headers() }));
      this.email.set(email);
      if (r.next === 'signedin' && r.session) { write(SESSION, r.session); await this.enter(); return; }
      if (r.next !== 'login') { this.error.set("We couldn't find a partner account for that email. Apply for trade prices first, or check the address."); return; }
      this.phase.set('code'); this.info.set(r.message ?? 'We emailed you a code.'); this.focusCode();
    });
  }

  protected async verify(form: HTMLFormElement): Promise<void> {
    if (!form.reportValidity()) return;
    const code = String(new FormData(form).get('code') ?? '').replace(/\D/g, '');
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ partner: boolean; session?: string; trustedBrowser?: string }>('/api/trade/verify', { email: this.email(), code }, { headers: this.headers() }));
      if (!r.partner || !r.session) { this.error.set('That code was for an application, not a partner account.'); return; }
      write(SESSION, r.session);
      if (r.trustedBrowser) write(TRUSTED, r.trustedBrowser);
      await this.enter();
    });
  }

  protected async resend(): Promise<void> {
    await this.run(async () => { await firstValueFrom(this.http.post('/api/trade/resend', { email: this.email() })); this.info.set('A new code is on its way.'); });
  }

  protected async signOut(): Promise<void> {
    try { await firstValueFrom(this.http.post('/api/trade/signout', {}, { headers: this.headers() })); } catch { /* signing out locally is enough */ }
    remove(SESSION); remove(TRUSTED);
    this.accounts.set([]); this.basket.set({}); this.phase.set('email'); this.email.set('');
  }

  private async enter(): Promise<boolean> {
    try {
      const r = await firstValueFrom(this.http.get<{ email: string; accounts: Account[] }>('/api/trade/me', { headers: this.headers() }));
      this.accounts.set(r.accounts); this.maskedEmail.set(r.email);
      const approvedFirst = r.accounts.findIndex(a => a.status === 'Approved');
      this.storeIndex.set(approvedFirst >= 0 ? approvedFirst : 0);
      this.phase.set('in'); this.tab.set('shop');
      void this.loadSide();
      if (this.returning) { this.returning = false; history.replaceState(null, '', location.pathname); await this.confirmPayments(); }
      return true;
    } catch { remove(SESSION); this.phase.set('email'); return false; }
  }

  private async refresh(): Promise<void> {
    try { const r = await firstValueFrom(this.http.get<{ email: string; accounts: Account[] }>('/api/trade/me', { headers: this.headers() })); this.accounts.set(r.accounts); } catch { /* keep what is shown */ }
    await this.loadSide();
  }

  protected pickStore(i: number): void { this.storeIndex.set(i); this.basket.set({}); this.checkoutOpen.set(false); void this.loadSide(); }

  private async loadSide(): Promise<void> {
    const a = this.account(); if (!a) return;
    const base = `/api/trade/portal/${a.store}`;
    const h = { headers: this.headers() };
    const [notices, messages, wallet] = await Promise.all([
      firstValueFrom(this.http.get<Notice[]>(`${base}/notices`, h)).catch(() => [] as Notice[]),
      firstValueFrom(this.http.get<Message[]>(`${base}/messages`, h)).catch(() => [] as Message[]),
      a.status === 'Approved' ? firstValueFrom(this.http.get<WalletRow[]>(`${base}/wallet`, h)).catch(() => [] as WalletRow[]) : Promise.resolve([] as WalletRow[]),
    ]);
    this.notices.set(notices); this.messages.set(messages); this.wallet.set(wallet);
    this.seenNotices.set(Number(read(`seen-${a.store}`) ?? 0));
  }

  private async confirmPayments(): Promise<void> {
    for (const a of this.accounts().filter(x => x.status === 'Approved')) {
      try { const r = await firstValueFrom(this.http.post<{ message: string }>(`/api/trade/portal/${a.store}/confirm`, {}, { headers: this.headers() })); this.flash(r.message); } catch { /* shown on the next refresh */ }
    }
    await this.refresh();
  }

  // ------------------------------------------------------------------ shop

  protected qty(id: number): number { return this.basket()[id] ?? 0; }
  protected change(id: number, by: number): void {
    const next = { ...this.basket() }; next[id] = Math.max(0, Math.min(9999, (next[id] ?? 0) + by)); if (!next[id]) delete next[id]; this.basket.set(next);
  }
  protected setQty(id: number, v: string): void { const n = Math.max(0, Math.min(9999, Math.floor(Number(v) || 0))); const next = { ...this.basket() }; if (n) next[id] = n; else delete next[id]; this.basket.set(next); }
  protected pct(p: Price): number { return p.retail > p.price ? Math.round((100 * (p.retail - p.price)) / p.retail) : 0; }

  protected openCheckout(): void { this.error.set(''); this.payWith.set((this.account()?.wallet ?? 0) >= this.subtotal() ? 'wallet' : 'online'); this.checkoutOpen.set(true); }

  protected async placeOrder(): Promise<void> {
    const a = this.account(); if (!a) return;
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ number: string; total: number; status: string; payUrl: string | null; message: string }>(`/api/trade/portal/${a.store}/order`,
        { lines: this.lines().map(l => ({ productId: l.p.id, quantity: l.qty })), pay: this.payWith(), note: this.note() || null }, { headers: this.headers() }));
      if (r.payUrl) { location.href = r.payUrl; return; }
      this.basket.set({}); this.checkoutOpen.set(false); this.note.set('');
      this.flash(r.message); this.tab.set('orders');
      await this.refresh();
    });
  }

  // ------------------------------------------------------------------ wallet

  protected async fund(custom: string): Promise<void> {
    const a = this.account(); if (!a) return;
    const amount = custom ? Number(custom.replace(/[^\d.]/g, '')) : this.topUp() ?? 0;
    if (!amount || amount < 1000) { this.error.set('Enter at least ₦1,000.'); return; }
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ payUrl: string }>(`/api/trade/portal/${a.store}/fund`, { amount }, { headers: this.headers() }));
      location.href = r.payUrl;
    });
  }

  // ------------------------------------------------------------------ messages and notices

  protected async send(): Promise<void> {
    const a = this.account(); const text = this.draft().trim(); if (!a || !text) return;
    await this.run(async () => {
      this.messages.set(await firstValueFrom(this.http.post<Message[]>(`/api/trade/portal/${a.store}/messages`, { text }, { headers: this.headers() })));
      this.draft.set(''); setTimeout(() => this.thread()?.nativeElement.scrollTo({ top: 1e9 }), 0);
    });
  }

  protected openTab(t: Tab): void {
    this.tab.set(t); this.error.set('');
    if (t === 'messages') setTimeout(() => this.thread()?.nativeElement.scrollTo({ top: 1e9 }), 0);
  }

  protected toggleBell(): void {
    this.bellOpen.update(v => !v);
    const a = this.account(); if (a) { this.seenNotices.set(this.notices().length); write(`seen-${a.store}`, String(this.notices().length)); }
  }

  // ------------------------------------------------------------------ helpers

  protected naira(v: number): string { return '₦' + (v ?? 0).toLocaleString('en-NG', { maximumFractionDigits: 2 }); }
  protected when(iso: string): string { const d = new Date(iso); return d.toLocaleDateString('en-NG', { day: 'numeric', month: 'short' }) + ', ' + d.toLocaleTimeString('en-NG', { hour: 'numeric', minute: '2-digit' }); }
  protected initials(s: string): string { return s.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join(''); }

  private flash(text: string): void { this.toast.set(text); setTimeout(() => { if (this.toast() === text) this.toast.set(''); }, 6000); }
  private focusCode(): void { setTimeout(() => this.codeInput()?.nativeElement.focus(), 50); }

  private headers(): HttpHeaders {
    let h = new HttpHeaders();
    const s = read(SESSION); if (s) h = h.set('X-Partner-Session', s);
    const t = read(TRUSTED); if (t) h = h.set('X-Trusted-Browser', t);
    return h;
  }

  private async run(work: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.error.set('');
    try { await work(); }
    catch (e) {
      const err = e as HttpErrorResponse;
      if (err.status === 401) { remove(SESSION); this.phase.set('email'); this.error.set('Please sign in again.'); return; }
      this.error.set(err.status === 429 ? 'Too many attempts. Please wait a few minutes.' : err.error?.title ?? err.error?.detail ?? 'Something went wrong. Please try again.');
    } finally { this.busy.set(false); }
  }
}

function read(k: string): string | null { try { return localStorage.getItem(k); } catch { return null; } }
function write(k: string, v: string): void { try { localStorage.setItem(k, v); } catch { /* private mode */ } }
function remove(k: string): void { try { localStorage.removeItem(k); } catch { /* private mode */ } }
