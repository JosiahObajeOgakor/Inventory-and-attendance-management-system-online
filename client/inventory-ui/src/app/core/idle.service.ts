import { Injectable, NgZone, inject, signal } from '@angular/core';
import { Auth } from './auth.service';

/**
 * Admin sessions only, as in the desktop app: after 5 idle minutes a countdown warns for 3 minutes, then the admin is
 * signed out. Clerks are never idled out. (The server session also slides out after 30 minutes without any request.)
 */
@Injectable({ providedIn: 'root' })
export class Idle {
  private readonly auth = inject(Auth);
  private readonly zone = inject(NgZone);
  static readonly IDLE_MS = 5 * 60_000;
  static readonly WARN_S = 3 * 60;

  readonly warning = signal(false);
  readonly secondsLeft = signal(Idle.WARN_S);
  private idleTimer: ReturnType<typeof setTimeout> | null = null;
  private tick: ReturnType<typeof setInterval> | null = null;
  private started = false;

  start(): void {
    if (this.started) return;
    this.started = true;
    for (const ev of ['pointerdown', 'keydown', 'wheel', 'touchstart']) window.addEventListener(ev, this.activity, { passive: true });
    this.arm();
  }

  stop(): void {
    this.started = false;
    for (const ev of ['pointerdown', 'keydown', 'wheel', 'touchstart']) window.removeEventListener(ev, this.activity);
    this.clearTimers();
    this.warning.set(false);
  }

  stay(): void { this.warning.set(false); this.arm(); }

  private readonly activity = () => { if (!this.warning()) this.arm(); };

  private arm(): void {
    this.clearTimers();
    if (!this.auth.isAdmin()) return;
    this.zone.runOutsideAngular(() => { this.idleTimer = setTimeout(() => this.warn(), Idle.IDLE_MS); });
  }

  private warn(): void {
    if (!this.auth.isAdmin()) return;
    this.secondsLeft.set(Idle.WARN_S);
    this.warning.set(true);
    this.tick = setInterval(() => {
      this.secondsLeft.update(s => s - 1);
      if (this.secondsLeft() <= 0) { this.stop(); void this.auth.logout(); }
    }, 1000);
  }

  private clearTimers(): void {
    if (this.idleTimer) clearTimeout(this.idleTimer);
    if (this.tick) clearInterval(this.tick);
    this.idleTimer = this.tick = null;
  }
}
