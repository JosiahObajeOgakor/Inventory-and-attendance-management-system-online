import { Component, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

/** One product as the shop shows it (GET /api/webchat/products): today's retail price from the stock system; exact counts stay private. */
interface ShopItem { id: number; name: string; unit: string; price: number; few: boolean; hasImage: boolean; }

/**
 * The shop: what is in stock right now, with today's prices. Customers pick quantities here, then one tap opens WhatsApp with the whole
 * basket typed out ("Hi! I'd like to order 2 … and 1 …"), which the sales assistant turns straight into an order summary.
 */
@Component({
  selector: 'app-shop',
  imports: [RevealDirective],
  templateUrl: './shop.html',
  styleUrl: './shop.scss',
})
export class Shop {
  private readonly http = inject(HttpClient);
  protected readonly items = signal<ShopItem[]>([]);
  protected readonly state = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly basket = signal<Record<number, number>>({});

  protected readonly lines = computed(() => this.items().filter(i => (this.basket()[i.id] ?? 0) > 0).map(i => ({ item: i, qty: this.basket()[i.id] })));
  protected readonly count = computed(() => this.lines().reduce((n, l) => n + l.qty, 0));
  protected readonly total = computed(() => this.lines().reduce((s, l) => s + l.qty * l.item.price, 0));
  protected readonly orderHref = computed(() =>
    whatsappHref(`Hi! I'd like to order ${this.lines().map(l => `${l.qty} ${l.item.name}`).join(' and ')}`));
  protected readonly askHref = whatsappHref("Hi! What do you have in stock today?");

  constructor() {
    // Prices and stock change during the day, so the prerendered page never bakes them in: the browser asks on load.
    if (!isPlatformBrowser(inject(PLATFORM_ID))) return;
    this.http.get<ShopItem[]>('/api/webchat/products').subscribe({
      next: list => { this.items.set(list); this.state.set('ready'); },
      error: () => this.state.set('error'),
    });
  }

  protected qty(id: number): number { return this.basket()[id] ?? 0; }
  protected change(id: number, by: number): void {
    this.basket.update(b => {
      const next = Math.max(0, Math.min(999, (b[id] ?? 0) + by));
      const copy = { ...b };
      if (next === 0) delete copy[id]; else copy[id] = next;
      return copy;
    });
  }
  protected setQty(id: number, value: string): void {
    const n = Math.max(0, Math.min(999, Math.floor(Number(value) || 0)));
    this.basket.update(b => { const copy = { ...b }; if (n === 0) delete copy[id]; else copy[id] = n; return copy; });
  }
  protected clear(): void { this.basket.set({}); }

  protected naira(n: number): string { return '₦' + n.toLocaleString('en-NG', { maximumFractionDigits: 2 }); }
  protected image(i: ShopItem): string | null { return i.hasImage ? `/api/webchat/products/${i.id}/image` : null; }
  protected initials(name: string): string { return name.split(/\s+/).filter(w => /[a-z]/i.test(w)).slice(0, 2).map(w => w[0]).join('').toUpperCase(); }
}
