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
import { AssistantPanel } from '../features/assistant/assistant';
import { ClerkWelcome } from '../features/attendance/clerk-welcome';

interface NavItem { label: string; path: string; icon: string; adminOnly?: boolean; ceoOnly?: boolean; needs?: 'hasPriceLists'; }
interface NavGroup { title: string; icon: string; items: NavItem[]; }

const NAV: NavGroup[] = [
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
    { label: 'Suppliers', path: '/suppliers', icon: 'supplier', adminOnly: true },
    { label: 'Purchases', path: '/purchases', icon: 'purchase', adminOnly: true },
  ] },
  { title: 'Money', icon: 'finance', items: [
    { label: 'Finance', path: '/finance', icon: 'finance', adminOnly: true },
    { label: 'Analytics', path: '/analytics', icon: 'spark', adminOnly: true },
    { label: 'Expenses', path: '/expenses', icon: 'cash', adminOnly: true },
    { label: 'Rebates', path: '/rebates', icon: 'gift', adminOnly: true },
  ] },
  { title: 'People', icon: 'users', items: [
    { label: 'Team', path: '/team', icon: 'clock', adminOnly: true },
    { label: 'People & access', path: '/users', icon: 'users', ceoOnly: true },
  ] },
  { title: 'System', icon: 'settings', items: [
    { label: 'Company & documents', path: '/company', icon: 'settings', adminOnly: true },
    { label: 'Activity log', path: '/activity', icon: 'audit', adminOnly: true },
    { label: 'Database storage', path: '/storage', icon: 'stock', ceoOnly: true },
  ] },
];

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
  protected readonly checkinOpen = signal(false);
  protected readonly checkingIn = signal(false);

  protected readonly navOpen = signal(false);
  protected readonly menuOpen = signal(false);
  protected readonly groups = computed(() =>
    NAV.map(g => ({ ...g, items: g.items.filter(i => (!i.adminOnly || this.auth.isAdmin()) && (!i.ceoOnly || this.auth.isCeo()) && (!i.needs || this.auth.flags()[i.needs])) })).filter(g => g.items.length));
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
    // A clerk is asked to clock on once per day, when they first arrive. The request runs untracked: an HTTP call started inside an effect
    // would otherwise make the effect depend on the spinner's own state and re-fire on every show/hide (an endless loading loop on a slow line).
    effect(() => { if (this.auth.me() && !this.auth.isCeo()) untracked(() => void this.askCheckIn()); });
  }

  private async askCheckIn() {
    try {
      const key = 'declined-' + new Date().toDateString();
      if (sessionStorage.getItem(key)) return;
      if ((await this.api2.attendanceToday()).checkIns === 0) this.checkinOpen.set(true);
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
