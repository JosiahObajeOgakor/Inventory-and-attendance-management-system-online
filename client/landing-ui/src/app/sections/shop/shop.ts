import { I18n } from '../../core/i18n';
import { Component, ElementRef, PLATFORM_ID, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ShopStore, waLink } from '../../core/stores';
import { Track } from '../../core/track';
import { Pixels } from '../../core/pixels';
import { whatsappHref } from '../../core/whatsapp';

/** One product as the shop shows it (GET /api/webchat/products): today's retail price from the stock system; exact counts stay private. */
interface ShopItem { id: number; name: string; unit: string; price: number; few: boolean; hasImage: boolean; category: string | null; }
/** A product's page (GET /api/webchat/products/{id}). */
interface ShopProduct extends ShopItem {
  inStock: boolean; species: string | null; lifeStage: string | null; proteinPct: number | null; fatPct: number | null; fiberPct: number | null; moisturePct: number | null;
  nutritionSummary: string | null; description: string | null; feedingGuide: string | null;
}

const PAGE = 12;

/**
 * One store's shelf: what is in stock right now, with today's prices, searchable and filterable by category. Each product opens its own page
 * (description, analysis, feeding guide). The basket goes either to WhatsApp, typed out ("Hi! I'd like to order 2 … and 1 …"), or to a secure
 * AlatPay page to pay now by card, transfer or USSD.
 */
@Component({
  selector: 'app-shop',
  templateUrl: './shop.html',
  styleUrl: './shop.scss',
})
export class Shop {
  protected readonly t = inject(I18n).t;
  private readonly http = inject(HttpClient);
  private readonly track = inject(Track);
  private readonly pixels = inject(Pixels);
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

  // ---- product page
  private readonly productDialog = viewChild<ElementRef<HTMLDialogElement>>('productDialog');
  protected readonly product = signal<ShopProduct | null>(null);
  protected readonly productError = signal('');
  protected readonly feedingLines = computed(() => (this.product()?.feedingGuide ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean));
  protected readonly analysis = computed(() => {
    const p = this.product(); if (!p) return [];
    return ([['Protein', p.proteinPct], ['Fat', p.fatPct], ['Fibre', p.fiberPct], ['Moisture', p.moisturePct]] as const).filter(([, v]) => v !== null && v !== undefined);
  });

  // ---- checkout (pay online through AlatPay)
  private readonly payDialog = viewChild<ElementRef<HTMLDialogElement>>('payDialog');
  protected readonly zones = computed(() => this.store()?.delivery ?? []);
  protected readonly canPayOnline = computed(() => this.store()?.payOnline ?? false);
  protected readonly name = signal('');
  protected readonly phone = signal('');
  protected readonly email = signal('');
  protected readonly zone = signal('');
  protected readonly address = signal('');
  protected readonly tried = signal(false);
  protected readonly paying = signal(false);
  protected readonly payError = signal('');

  /** Exactly the server's sum (DocumentCalculator + delivery after VAT), so the amount on the button is the amount AlatPay asks for. */
  protected readonly vatRate = computed(() => this.store()?.vatRate ?? 0);
  protected readonly vat = computed(() => Math.round(this.total() * this.vatRate()) / 100);
  protected readonly deliveryFee = computed(() => this.zones().find(z => z.name === this.zone())?.fee ?? 0);
  protected readonly grandTotal = computed(() => this.total() + this.vat() + this.deliveryFee());
  protected readonly errors = computed(() => {
    const e: Record<string, string> = {};
    if (this.name().trim().length < 2) e['name'] = 'Enter your name.';
    const digits = this.phone().replace(/\D/g, '');
    if (!(digits.length === 11 && digits.startsWith('0')) && !(digits.length === 13 && digits.startsWith('234')) && !(digits.length === 10 && !digits.startsWith('0'))) e['phone'] = 'Enter a Nigerian mobile number, e.g. 0803 123 4567.';
    if (this.email().trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(this.email().trim())) e['email'] = 'Check the email address.';
    if (this.zone() && this.address().trim().length < 5) e['address'] = 'Enter the delivery address.';
    return e;
  });
  protected err(field: string): string | null { return this.tried() ? this.errors()[field] ?? null : null; }
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
    if (n > 0 && this.count() === 0) this.track.event('basket_start', this.storeKey(), undefined, true);
    this.basket.update(b => { const copy = { ...b }; if (n === 0) delete copy[id]; else copy[id] = n; return copy; });
  }
  protected clear(): void { this.basket.set({}); }
  protected search(v: string): void { this.query.set(v); this.shown.set(PAGE); }
  protected pick(c: string | null): void { this.category.set(c); this.shown.set(PAGE); }
  protected more(): void { this.shown.update(n => n + PAGE); }
  protected sentToWhatsApp(): void { this.track.event('basket_whatsapp', this.storeKey(), this.total()); }

  protected open(i: ShopItem): void {
    this.product.set({ ...i, inStock: true, species: null, lifeStage: null, proteinPct: null, fatPct: null, fiberPct: null, moisturePct: null, nutritionSummary: null, description: null, feedingGuide: null });
    this.productError.set('');
    this.productDialog()?.nativeElement.showModal();
    this.pixels.viewContent(i.name, i.price);
    this.http.get<ShopProduct>(`/api/webchat/products/${i.id}`, { params: { store: this.storeKey() } }).subscribe({
      next: p => this.product.set(p),
      error: () => this.productError.set("We couldn't load the full details just now."),
    });
  }
  protected closeProduct(): void { this.productDialog()?.nativeElement.close(); }

  protected openPay(): void { this.payError.set(''); this.tried.set(false); this.payDialog()?.nativeElement.showModal(); }
  protected closePay(): void { if (!this.paying()) this.payDialog()?.nativeElement.close(); }
  /** Closes a dialog when its dark backdrop is clicked. Returns nothing on purpose: a handler that returns false cancels the click (and a form's submit). */
  protected productBackdrop(e: MouseEvent): void { if (e.target === e.currentTarget) this.closeProduct(); }
  protected payBackdrop(e: MouseEvent): void { if (e.target === e.currentTarget) this.closePay(); }

  protected pay(): void {
    this.tried.set(true);
    if (this.paying() || Object.keys(this.errors()).length) return;
    this.paying.set(true); this.payError.set('');
    const body = {
      name: this.name().trim(), phone: this.phone().trim(), email: this.email().trim() || null, zone: this.zone() || null,
      address: this.zone() ? this.address().trim() : null, lines: this.lines().map(l => ({ productId: l.item.id, quantity: l.qty })),
    };
    this.http.post<{ url: string; number: string; total: number }>('/api/webchat/checkout', body, { params: { store: this.storeKey() } }).subscribe({
      next: r => { location.href = r.url; },
      error: (e: HttpErrorResponse) => {
        this.paying.set(false);
        this.payError.set(e.status === 0 ? "We couldn't reach the payment service. Check your connection and try again."
          : e.error?.title ?? e.error?.detail ?? "We couldn't start the payment. Please try again, or send your basket on WhatsApp.");
      },
    });
  }
  protected forWhom(p: ShopProduct): string { return [p.species, p.lifeStage].filter(x => !!x).join(' · '); }
  protected label(c: string): string { return c.charAt(0).toUpperCase() + c.slice(1).toLowerCase(); }
  protected naira(n: number): string { return '₦' + n.toLocaleString('en-NG', { maximumFractionDigits: 2 }); }
  protected image(i: { id: number; hasImage: boolean }): string | null { return i.hasImage ? `/api/webchat/products/${i.id}/image?store=${this.storeKey()}` : null; }
  protected initials(name: string): string { return name.split(/\s+/).filter(w => /[a-z]/i.test(w)).slice(0, 2).map(w => w[0]).join('').toUpperCase(); }
}
