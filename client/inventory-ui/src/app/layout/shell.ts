import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../core/auth.service';
import { Idle } from '../core/idle.service';
import { messageOf } from '../core/api.service';
import { Api2 } from '../core/api-more';
import { I18n } from '../core/i18n.service';
import { Icon } from '../shared/icon';
import { Modal } from '../shared/modal';
import { Toasts } from '../shared/feedback';
import { HttpClient } from '@angular/common/http';
import { APPS, APP_GROUPS, MARKETING_APPS } from '../core/product-catalog';
import { AssistantPanel } from '../features/assistant/assistant';
import { ClerkWelcome } from '../features/attendance/clerk-welcome';

/** Module-level so it survives the shell being destroyed and re-created (e.g. on a business switch). */
let checkInAskedFor: string | null = null;

interface NavItem { label: string; path: string; icon: string; adminOnly?: boolean; ceoOnly?: boolean; needs?: 'hasPriceLists'; /** A record-keeping app: offered only when the server has it switched on for this business. */ app?: string; /** A newer feature being tested with K9 Cravings: visible everywhere, usable only there. */ preview?: boolean; }
interface NavGroup { title: string; icon: string; items: NavItem[]; }

const BUILT: NavGroup[] = [
  { title: 'Counter', icon: 'sale', items: [
    { label: 'Dashboard', path: '/dashboard', icon: 'dashboard' },
    { label: 'Sales', path: '/sales', icon: 'sale' },
    { label: 'Stock', path: '/stock', icon: 'stock' },
    { label: 'Products', path: '/products', icon: 'product' },
    { label: 'Price book', path: '/price-book', icon: 'tag', needs: 'hasPriceLists' },
    { label: 'Serial numbers', path: '/serials', icon: 'barcode' },
  ] },
  { title: 'Trade', icon: 'customer', items: [
    { label: 'Quotations', path: '/quotations', icon: 'file' },
    { label: 'Waybills', path: '/waybills', icon: 'supplier' },
    { label: 'Customers', path: '/customers', icon: 'customer', adminOnly: true },
      { label: 'Debtors', path: '/debtors', icon: 'cash', adminOnly: true },
    { label: 'Campaigns', path: '/campaigns', icon: 'spark', adminOnly: true, preview: true },
    { label: 'Suppliers', path: '/suppliers', icon: 'supplier', adminOnly: true },
    { label: 'Purchases', path: '/purchases', icon: 'purchase', adminOnly: true },
  ] },
  { title: 'Money', icon: 'finance', items: [
    { label: 'Finance', path: '/finance', icon: 'finance', adminOnly: true },
    { label: 'Analytics', path: '/analytics', icon: 'spark', adminOnly: true },
    { label: 'Expenses', path: '/expenses', icon: 'cash', adminOnly: true },
    { label: 'Rebates', path: '/rebates', icon: 'gift', adminOnly: true },
    { label: 'Accounting', path: '/accounting', icon: 'audit', adminOnly: true, preview: true },
    { label: 'Tax', path: '/tax', icon: 'cash', adminOnly: true, preview: true },
    { label: 'Bank reconciliation', path: '/banking', icon: 'cash', adminOnly: true, preview: true },
    { label: 'Budgets', path: '/budgets', icon: 'spark', adminOnly: true, preview: true },
    { label: 'Fixed assets', path: '/fixed-assets', icon: 'stock', adminOnly: true, preview: true },
    { label: 'Currencies', path: '/currencies', icon: 'finance', adminOnly: true, preview: true },
    { label: 'Approvals', path: '/approvals', icon: 'audit', adminOnly: true, preview: true },
  ] },
  { title: 'People', icon: 'users', items: [
    { label: 'Team', path: '/team', icon: 'clock', adminOnly: true },
    { label: 'People & access', path: '/users', icon: 'users', ceoOnly: true },
  ] },
  { title: 'System', icon: 'settings', items: [
    { label: 'Company & documents', path: '/company', icon: 'settings', adminOnly: true },
    { label: 'Branches', path: '/branches', icon: 'stock', adminOnly: true, preview: true },
    { label: 'Activity log', path: '/activity', icon: 'audit', adminOnly: true },
    { label: 'Database storage', path: '/storage', icon: 'stock', ceoOnly: true },
  ] },
];

/** The core sections, plus the record-keeping apps: an app of an existing section joins it, the rest get a section of their own just before System. */
const NAV: NavGroup[] = (() => {
  const nav = BUILT.map(g => ({ ...g, items: [...g.items] }));
  for (const g of APP_GROUPS) {
    const items: NavItem[] = APPS.filter(a => a.group === g.group).map(a => ({ label: a.label, path: a.path ?? '/apps/' + a.key, icon: a.icon, adminOnly: true, app: a.key, preview: true }));
    const own = nav.find(x => x.title === g.group);
    if (own) own.items.push(...items);
    else nav.splice(nav.findIndex(x => x.title === 'System'), 0, { title: g.group, icon: g.icon, items });
  }
  return nav;
})();

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Icon, Modal, AssistantPanel, ClerkWelcome],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  protected readonly auth = inject(Auth);
  protected readonly idle = inject(Idle);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  private readonly api2 = inject(Api2);
  protected readonly i18n = inject(I18n);
  private readonly http = inject(HttpClient);
  /** The record-keeping apps this installation has switched on (administrators only; nobody else sees them). */
  private readonly enabledApps = signal<string[]>([]);
  protected readonly checkinOpen = signal(false);
  protected readonly checkingIn = signal(false);

  protected readonly navOpen = signal(false);
  protected readonly menuOpen = signal(false);
  protected readonly groups = computed(() =>
    NAV.map(g => ({ ...g, items: g.items.filter(i => (!i.adminOnly || this.auth.isAdmin()) && (!i.ceoOnly || this.auth.isCeo()) && (!i.needs || this.auth.flags()[i.needs]) && (!i.app || this.locked(i) || this.enabledApps().includes(i.app))) })).filter(g => g.items.length));
  /** True for a newer feature in a business that doesn't have it yet. Campaigns and the Marketing apps open with Marketing alone. */
  protected locked(i: NavItem): boolean {
    const f = this.auth.flags();
    const marketing = i.path === '/campaigns' || (!!i.app && MARKETING_APPS.includes(i.app));
    return !!i.preview && !f.hasNewFeatures && !(marketing && f.hasMarketing);
  }
  protected readonly initials = computed(() => (this.auth.me()?.fullName ?? '?').split(/\s+/).map(w => w[0]).slice(0, 2).join('').toUpperCase());
  protected readonly countdown = computed(() => {
    const s = this.idle.secondsLeft();
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  });

  // ---- accordion: ONE group open at a time. It's the group holding the current page, until the person opens another one
  // (which closes the rest) or closes it. Moving to a page re-opens that page's group, so you always see where you are.
  private readonly url = signal(this.router.url);
  /** undefined = follow the current page; null = everything closed; a title = that group open. */
  private readonly chosen = signal<string | null | undefined>(undefined);
  private readonly currentGroup = computed(() =>
    this.groups().find(g => g.items.some(i => this.url().startsWith(i.path)))?.title ?? this.groups()[0]?.title ?? null);
  protected isOpen(g: NavGroup): boolean {
    const c = this.chosen();
    return (c === undefined ? this.currentGroup() : c) === g.title;
  }
  protected toggle(g: NavGroup): void {
    this.chosen.set(this.isOpen(g) ? null : g.title);
  }

  // ---- logo: the business's uploaded logo when it has one, else the bundled one; changes when the business is switched
  private readonly uploaded = signal(false);
  private readonly logoBroken = signal(false);
  protected readonly logoStamp = signal(Date.now());
  protected readonly logoSrc = computed(() => (this.logoBroken() ? '' : this.uploaded() ? '/api/company/assets/logo?v=' + this.logoStamp() : 'logo-' + this.auth.company() + '.jpeg'));
  protected logoFailed() { this.logoBroken.set(true); }

  constructor() {
    this.router.events.subscribe(e => { if (e instanceof NavigationEnd) { this.url.set(this.router.url); this.chosen.set(undefined); } });
    try { localStorage.removeItem('nav-open'); } catch { /* private mode */ }   // the old "many groups open" memory
    effect(() => {
      const key = this.auth.company(); if (!this.auth.me()) return;
      untracked(async () => {
        this.logoBroken.set(false); this.uploaded.set(false); this.logoStamp.set(Date.now());
        try { this.uploaded.set((await this.api2.companyProfile()).assets.includes('logo')); } catch { /* the bundled logo is used */ }
        void key;
      });
    });
    effect(() => { if (this.auth.isAdmin()) this.idle.start(); else this.idle.stop(); });
    effect(() => {
      if (!this.auth.me() || !this.auth.isAdmin()) { this.enabledApps.set([]); return; }
      this.http.get<string[]>('/api/apps/enabled').subscribe({ next: a => this.enabledApps.set(a), error: () => this.enabledApps.set([]) });
    });
    // A clerk is asked to clock on once per day, when they first arrive. The request runs untracked: an HTTP call started inside an effect
    // would otherwise make the effect depend on the spinner's own state and re-fire on every show/hide (an endless loading loop on a slow line).
    // At most once per person per day: the effect re-runs whenever `me` is replaced (and the shell can be rebuilt), and each re-run
    // used to fire another /api/attendance/today.
    effect(() => {
      const me = this.auth.me();
      if (!me || this.auth.isCeo()) return;
      const key = me.username + '|' + new Date().toDateString();
      if (checkInAskedFor === key) return;
      checkInAskedFor = key;
      untracked(() => void this.askCheckIn(key));
    });
  }

  private async askCheckIn(key: string) {
    try {
      if (sessionStorage.getItem('declined-' + new Date().toDateString()) || sessionStorage.getItem('clocked-' + key)) return;
      if ((await this.api2.attendanceToday()).checkIns === 0) this.checkinOpen.set(true);
      else sessionStorage.setItem('clocked-' + key, '1');
    } catch { /* attendance must never block the counter */ }
  }

  protected async checkIn() {
    this.checkingIn.set(true);
    try { await this.api2.checkIn(); this.checkinOpen.set(false); this.toasts.ok('You are clocked in. Have a good day.'); } catch (e) { this.toasts.error(messageOf(e)); } finally { this.checkingIn.set(false); }
  }

  protected async notNow() {
    this.checkinOpen.set(false);
    try { sessionStorage.setItem('declined-' + new Date().toDateString(), '1'); await this.api2.declineCheckIn(); } catch { /* not worth interrupting */ }
  }

  protected toggleLanguage() { this.menuOpen.set(false); void this.i18n.set(this.i18n.code() === 'ig' ? 'en' : 'ig'); }

  protected go() { this.navOpen.set(false); }

  protected async switchTo(key: string) {
    if (key === this.auth.company()) return;
    try {
      await this.auth.switchCompany(key);
      this.toasts.ok(`Now working in ${this.auth.companyName()}.`);
      // Every screen shows one business's data; reload the current route so nothing stale stays on screen.
      const url = this.router.url;
      await this.router.navigateByUrl('/', { skipLocationChange: true });
      await this.router.navigateByUrl(url.startsWith('/sales/') || url.startsWith('/purchases/') ? '/dashboard' : url);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async signOut() { this.idle.stop(); await this.auth.logout(); }
}

