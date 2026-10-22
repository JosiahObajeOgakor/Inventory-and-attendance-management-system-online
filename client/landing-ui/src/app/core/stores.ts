import { Injectable, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';

/** A store the website shows (GET /api/webchat/stores): the WhatsApp number its orders go to and its public contact details. */
export interface ShopZone { name: string; fee: number; eta: string | null; }
export interface SocialLink { platform: string; handle: string; url: string; }
export interface ShopStore {
  key: string; name: string; legalName: string; whatsApp: string | null; phone: string | null; email: string | null; address: string | null; website: string | null;
  openingHours: string | null; replyPromise: string | null; delivery: ShopZone[] | null; socials: SocialLink[] | null;
}

/** Loaded once in the browser; the prerendered page never bakes in contact details or stock. */
@Injectable({ providedIn: 'root' })
export class Stores {
  readonly list = signal<ShopStore[]>([]);
  readonly loaded = signal(false);

  constructor() {
    if (!isPlatformBrowser(inject(PLATFORM_ID))) return;
    inject(HttpClient).get<ShopStore[]>('/api/webchat/stores').subscribe({
      next: s => { this.list.set(s); this.loaded.set(true); },
      error: () => this.loaded.set(true),
    });
  }

  find(key: string): ShopStore | undefined { return this.list().find(s => s.key === key); }
}

/** A wa.me link to a given number with a message typed in. */
export function waLink(digits: string, message: string): string {
  return `https://wa.me/${digits}?text=${encodeURIComponent(message)}`;
}
