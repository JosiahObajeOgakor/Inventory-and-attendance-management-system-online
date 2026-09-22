import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Api } from './api.service';
import { Me } from './models';

/** Who is signed in and which business they have open. The session itself lives in an HttpOnly cookie. */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly api = inject(Api);
  private readonly router = inject(Router);

  readonly me = signal<Me | null>(null);
  readonly ready = signal(false);
  /** The business chosen on the sign-in screen (before there is a session). */
  readonly selected = signal<string>(storageGet('company') ?? 'chewypets');
  readonly isAdmin = computed(() => this.me()?.role === 'ADMIN');
  readonly company = computed(() => this.me()?.company ?? this.selected());
  readonly companyName = computed(() => this.me()?.companies.find(c => c.key === this.company())?.displayName ?? '');

  readonly flags = computed(() => {
    const c = this.me()?.companies.find(x => x.key === this.company());
    return { hasPriceLists: !!c?.hasPriceLists, buysGoods: !!c?.buysGoods };
  });

  constructor() {
    // The whole UI re-tints per business (see styles.scss).
    effect(() => { document.documentElement.dataset['company'] = this.company(); });
  }

  /** Called once at startup to pick up an existing session cookie. */
  async restore(): Promise<void> {
    try { this.me.set(await this.api.me()); } catch { this.me.set(null); }
    this.ready.set(true);
  }

  async login(username: string, password: string, company: string): Promise<Me> {
    const me = await this.api.login(username, password, company);
    storageSet('company', company);
    this.me.set(me);
    return me;
  }

  async switchCompany(company: string): Promise<void> {
    this.me.set(await this.api.switchCompany(company));
    this.selected.set(company);
    storageSet('company', company);
  }

  async logout(): Promise<void> {
    try { await this.api.logout(); } catch { /* the session may already be gone */ }
    this.clear();
  }

  /** Session ended (sign-out or a 401): forget the user and go to sign-in. */
  clear(): void {
    this.me.set(null);
    void this.router.navigate(['/login']);
  }
}

function storageGet(k: string): string | null { try { return localStorage.getItem(k); } catch { return null; } }
function storageSet(k: string, v: string): void { try { localStorage.setItem(k, v); } catch { /* private mode */ } }
