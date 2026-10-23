import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Pixels } from './pixels';

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
  private readonly pixels = inject(Pixels);

  event(event: SiteEvent, store = 'chewypets', amount?: number, oncePerPage = false): void {
    if (!this.browser) return;
    const key = `${event}:${store}`;
    if (oncePerPage && this.once.has(key)) return;
    this.once.add(key);
    // the same moments, for the ad platforms (only once the visitor has accepted their cookies)
    if (event === 'basket_start') this.pixels.addToCart(amount);
    else if (event === 'basket_whatsapp') { this.pixels.contact(); this.pixels.checkout(amount); }
    else if (event === 'checkout_start') this.pixels.checkout(amount);
    this.http.post('/api/webchat/track', { event, store, amount: amount ?? null }).subscribe({ error: () => { /* counting never gets in the way */ } });
  }
}
