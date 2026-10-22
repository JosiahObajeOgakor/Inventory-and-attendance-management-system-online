import { Component, PLATFORM_ID, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ShopStore, waLink } from '../../core/stores';
import { whatsappHref } from '../../core/whatsapp';

/** One product as the shop shows it (GET /api/webchat/products): today's retail price from the stock system; exact counts stay private. */
interface ShopItem { id: number; name: string; unit: string; price: number; few: boolean; hasImage: boolean; category: string | null; }

const PAGE = 12;

/**
 * One store's shelf: what is in stock right now, with today's prices, searchable and filterable by category. Customers pick quantities, then one
 * tap opens WhatsApp with the whole basket typed out ("Hi! I'd like to order 2 … and 1 …"), to the number that store takes orders on.
 */
@Component({
  selector: 'app-shop',
  templateUrl: './shop.html',
  styleUrl: './shop.scss',
})
export class Shop {
  private readonly http = inject(HttpClient);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  /** Which store; null while the store list is still loading (the landing brand is assumed). */
  readonly store = input<ShopStore | null>(null);
  readonly storeKey = input('chewypets');

  protected readonly items = signal<ShopItem[]>([]);
  protected readonly state = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly basket = signal<Record<number, number>>({});
  protected readonly query = signal('');
  protected readonly category = signal<string | null>(null);
  protected readonly shown = signal(PAGE);

  protected readonly categories = computed(() => [...new Set(this.items().map(i => i.category).filter((c): c is string => !!c))].sort());
  /** "DOG FOOD" -> "Dog food" for the filter chips. */
  protected label(c: string): string { return c.charAt(0).toUpperCase() + c.slice(1).toLowerCase(); }
  protected readonly filtered = computed(() => {
    const words = this.query().toLowerCase().split(/\s+/).filter(Boolean);
    const cat = this.category();
    return this.items().filter(i => (!cat || i.category === cat) && words.every(w => `${i.name} ${i.category ?? ''}`.toLowerCase().includes(w)));
  });
  protected readonly visible = computed(() => this.filtered().slice(0, this.shown()));

  protected readonly lines = computed(() => this.items().filter(i => (this.basket()[i.id] ?? 0) > 0).map(i => ({ item: i, qty: this.basket()[i.id] })));
  protected readonly count = computed(() => this.lines().reduce((n, l) => n + l.qty, 0));
  protected readonly total = computed(() => this.lines().reduce((s, l) => s + l.qty * l.item.price, 0));
  private readonly isMain = computed(() => this.storeKey() === 'chewypets');
  protected readonly orderHref = computed(() => this.link(`${this.greeting()} I'd like to order ${this.lines().map(l => `${l.qty} ${l.item.name}`).join(' and ')}`));
  protected readonly askHref = computed(() => this.link(`${this.greeting()} What do you have in stock today?`));

  constructor() {
    // Prices and stock change during the day, so the prerendered page never bakes them in: the browser asks when the shelf is shown.
    effect(() => {
      const key = this.storeKey();
      if (!this.browser) return;
      untracked(() => this.load(key));
    });
  }

  private load(key: string): void {
    this.state.set('loading');
    this.http.get<ShopItem[]>('/api/webchat/products', { params: { store: key } }).subscribe({
      next: list => { this.items.set(list); this.state.set('ready'); },
      error: () => this.state.set('error'),
    });
  }

  private greeting(): string { return this.isMain() ? 'Hi!' : `Hi ${this.store()?.name ?? 'there'}!`; }
  private link(text: string): string {
    const digits = this.store()?.whatsApp;
    return digits ? waLink(digits, text) : whatsappHref(text);
  }

  protected qty(id: number): number { return this.basket()[id] ?? 0; }
  protected change(id: number, by: number): void { this.put(id, (this.basket()[id] ?? 0) + by); }
  protected setQty(id: number, value: string): void { this.put(id, Math.floor(Number(value) || 0)); }
  private put(id: number, n: number): void {
    n = Math.max(0, Math.min(999, n));
    this.basket.update(b => { const copy = { ...b }; if (n === 0) delete copy[id]; else copy[id] = n; return copy; });
  }
  protected clear(): void { this.basket.set({}); }
  protected search(v: string): void { this.query.set(v); this.shown.set(PAGE); }
  protected pick(c: string | null): void { this.category.set(c); this.shown.set(PAGE); }
  protected more(): void { this.shown.update(n => n + PAGE); }

  protected naira(n: number): string { return '₦' + n.toLocaleString('en-NG', { maximumFractionDigits: 2 }); }
  protected image(i: ShopItem): string | null { return i.hasImage ? `/api/webchat/products/${i.id}/image?store=${this.storeKey()}` : null; }
  protected initials(name: string): string { return name.split(/\s+/).filter(w => /[a-z]/i.test(w)).slice(0, 2).map(w => w[0]).join('').toUpperCase(); }
}
