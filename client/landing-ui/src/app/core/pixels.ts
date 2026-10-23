import { Injectable, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';

/** The ad-platform IDs the server is set up with (Tracking:MetaPixelId, GoogleTagId, TikTokPixelId); empty means that platform is off. */
export interface TrackingIds { metaPixelId?: string | null; googleTagId?: string | null; tiktokPixelId?: string | null; }

type Fn = (...args: unknown[]) => void;
interface PixelWindow extends Window { fbq?: Fn & { queue?: unknown[]; loaded?: boolean; version?: string; push?: Fn; callMethod?: Fn }; _fbq?: unknown;
  dataLayer?: unknown[]; gtag?: Fn; ttq?: { load: (id: string) => void; page: () => void; track: (e: string, p?: object) => void } & Record<string, unknown>; }

const CONSENT_KEY = 'ads-consent';

/**
 * Meta Pixel, Google tag (GA4 / Google Ads) and TikTok Pixel, so ads on those platforms can be measured and retargeted.
 * Nothing loads until the visitor accepts (Nigeria Data Protection Act; the same banner covers EU visitors), and only for the platforms the server has IDs for.
 * Scripts are added from code (no inline script), so the site's Content-Security-Policy only has to name the three platforms' domains.
 */
@Injectable({ providedIn: 'root' })
export class Pixels {
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private ids: TrackingIds = {};
  private loaded = false;
  /** True when there is at least one platform set up and the visitor hasn't decided yet: the banner shows. */
  readonly ask = signal(false);

  configure(ids: TrackingIds | null | undefined): void {
    if (!this.browser || !ids) return;
    this.ids = {
      metaPixelId: /^\d{10,20}$/.test(ids.metaPixelId ?? '') ? ids.metaPixelId : null,
      googleTagId: /^(G|AW|GT)-[A-Z0-9]{4,20}$/.test(ids.googleTagId ?? '') ? ids.googleTagId : null,
      tiktokPixelId: /^[A-Z0-9]{10,30}$/i.test(ids.tiktokPixelId ?? '') ? ids.tiktokPixelId : null,
    };
    if (!this.any()) return;
    const decided = read(CONSENT_KEY);
    if (decided === 'yes') this.load();
    else if (decided !== 'no') this.ask.set(true);
  }

  decide(accept: boolean): void {
    write(CONSENT_KEY, accept ? 'yes' : 'no');
    this.ask.set(false);
    if (accept) this.load();
  }

  // ---- events (a no-op until the visitor has accepted)
  viewContent(name: string, value?: number): void { this.fire('ViewContent', 'view_item', 'ViewContent', { content_name: name, value, currency: 'NGN' }); }
  addToCart(value?: number): void { this.fire('AddToCart', 'add_to_cart', 'AddToCart', { value, currency: 'NGN' }); }
  checkout(value?: number): void { this.fire('InitiateCheckout', 'begin_checkout', 'InitiateCheckout', { value, currency: 'NGN' }); }
  purchase(value?: number): void { this.fire('Purchase', 'purchase', 'CompletePayment', { value, currency: 'NGN' }); }
  contact(): void { this.fire('Contact', 'contact', 'Contact', {}); }
  lead(): void { this.fire('Lead', 'generate_lead', 'SubmitForm', {}); }

  private any(): boolean { return !!(this.ids.metaPixelId || this.ids.googleTagId || this.ids.tiktokPixelId); }

  private fire(meta: string, google: string, tiktok: string, params: Record<string, unknown>): void {
    if (!this.loaded) return;
    const w = window as PixelWindow;
    const p = Object.fromEntries(Object.entries(params).filter(([, v]) => v !== undefined));
    try { w.fbq?.('track', meta, p); } catch { /* a blocked script never breaks the page */ }
    try { w.gtag?.('event', google, p); } catch { /* */ }
    try { w.ttq?.track(tiktok, p); } catch { /* */ }
  }

  private load(): void {
    if (this.loaded || !this.any()) return;
    this.loaded = true;
    const w = window as PixelWindow;
    if (this.ids.metaPixelId) {
      // Meta's own bootstrap, written as code: a queueing stub that the real script replaces.
      const fbq: PixelWindow['fbq'] = Object.assign((...args: unknown[]) => { fbq!.callMethod ? fbq!.callMethod(...args) : fbq!.queue!.push(args); }, { queue: [] as unknown[], loaded: true, version: '2.0' });
      fbq.push = fbq; w.fbq = fbq; w._fbq = fbq;
      script('https://connect.facebook.net/en_US/fbevents.js');
      fbq('init', this.ids.metaPixelId); fbq('track', 'PageView');
    }
    if (this.ids.googleTagId) {
      w.dataLayer = w.dataLayer || [];
      w.gtag = function () { w.dataLayer!.push(arguments); } as Fn;   // gtag needs the real arguments object
      script(`https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(this.ids.googleTagId)}`);
      w.gtag('js', new Date()); w.gtag('config', this.ids.googleTagId);
    }
    if (this.ids.tiktokPixelId) {
      // TikTok's official bootstrap, as code: an array that queues [method, ...args] until events.js takes over.
      const id = this.ids.tiktokPixelId;
      const ttq = [] as unknown as unknown[] & Record<string, unknown>;
      const methods = ['page', 'track', 'identify', 'instances', 'debug', 'on', 'off', 'once', 'ready', 'alias', 'group', 'enableCookie', 'disableCookie', 'holdConsent', 'revokeConsent', 'grantConsent'];
      const defer = (target: unknown[] & Record<string, unknown>, m: string) => { target[m] = (...args: unknown[]) => { target.push([m, ...args]); }; };
      ttq['methods'] = methods;
      for (const m of methods) defer(ttq, m);
      ttq['_i'] = { [id]: Object.assign([], { _u: 'https://analytics.tiktok.com/i18n/pixel/events.js' }) };
      ttq['_t'] = { [id]: +new Date() };
      ttq['_o'] = { [id]: {} };
      (w as unknown as Record<string, unknown>)['TiktokAnalyticsObject'] = 'ttq';
      w.ttq = ttq as unknown as PixelWindow['ttq'];
      script(`https://analytics.tiktok.com/i18n/pixel/events.js?sdkid=${encodeURIComponent(id)}&lib=ttq`);
      w.ttq!.page();
    }
  }
}

function script(src: string): void {
  const s = document.createElement('script');
  s.async = true; s.src = src;
  document.head.appendChild(s);
}
function read(k: string): string | null { try { return localStorage.getItem(k); } catch { return null; } }
function write(k: string, v: string): void { try { localStorage.setItem(k, v); } catch { /* private mode: asked again next visit */ } }
