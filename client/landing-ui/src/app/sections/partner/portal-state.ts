import { Injectable, signal } from '@angular/core';

/** Opens the partner portal from anywhere on the page (the trade section, the footer, a link to #partner, the return from AlatPay). */
@Injectable({ providedIn: 'root' })
export class PortalState {
  readonly isOpen = signal(false);
  /** Set when the application form found the email belongs to a partner: the portal carries on from the code step instead of sending another code. */
  readonly handover = signal<{ email: string; next: 'login' | 'signedin'; session?: string } | null>(null);

  open(): void { this.isOpen.set(true); }
  close(): void { this.isOpen.set(false); }
  continueSignIn(email: string, next: 'login' | 'signedin', session?: string): void { this.handover.set({ email, next, session }); this.isOpen.set(true); }
}
