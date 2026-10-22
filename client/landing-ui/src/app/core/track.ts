import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';

export type SiteEvent = 'site_view' | 'basket_start' | 'basket_whatsapp' | 'checkout_start';

/**
 * Counts what happens on the site (a visit, a basket started, a basket sent to WhatsApp, a checkout opened) for the Campaigns insights.
 * No cookies and nothing about the visitor is sent: just the event, the store and, for a basket, its value. A visit and a basket start count once per page view.
 */
@Injectable({ providedIn: 'root' })
export class Track {
  private readonly http = inject(HttpClient);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly once = new Set<string>();

  event(event: SiteEvent, store = 'chewypets', amount?: number, oncePerPage = false): void {
    if (!this.browser) return;
    const key = `${event}:${store}`;
    if (oncePerPage && this.once.has(key)) return;
    this.once.add(key);
    this.http.post('/api/webchat/track', { event, store, amount: amount ?? null }).subscribe({ error: () => { /* counting never gets in the way */ } });
  }
}
