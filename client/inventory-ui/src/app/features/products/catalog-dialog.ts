import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Api, messageOf } from '../../core/api.service';
import { CustomerLookup, Product } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';

export interface ChosenItem { id: number; name: string; }
type Tier = 'Distributor' | 'Wholesaler' | 'Retailer';

/**
 * Build a price list or picture catalog for a customer and send it: every item or just the chosen ones, at distributor / wholesale / retail
 * prices, as a PDF or a single image. Download it, share it straight into WhatsApp from the phone, email it, or send it from the business's
 * WhatsApp number (that last one only reaches someone who messaged the business in the last 24 hours — a WhatsApp rule).
 */
@Component({
  selector: 'app-catalog-dialog',
  imports: [Icon, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-modal [open]="open()" heading="Price list / catalog" [wide]="true" (closed)="closed.emit()">
      <div class="grid">
        <fieldset><legend class="label">Items</legend>
          <label class="check"><input type="radio" name="scope" [checked]="scope() === 'all'" (change)="scope.set('all')" /> All items{{ inStockOnly() ? ' in stock' : '' }}</label>
          <label class="check"><input type="radio" name="scope" [checked]="scope() === 'chosen'" (change)="scope.set('chosen')" /> Only the items I choose ({{ chosen().length }})</label>
          @if (scope() === 'all') { <label class="check sub"><input type="checkbox" [checked]="!inStockOnly()" (change)="inStockOnly.set(!$any($event.target).checked)" /> Include items that are out of stock</label> }
        </fieldset>
        <fieldset><legend class="label">Prices</legend>
          @for (t of tiers; track t.value) { <label class="check"><input type="radio" name="tier" [checked]="tier() === t.value" (change)="tier.set(t.value)" /> {{ t.label }}</label> }
        </fieldset>
        <fieldset><legend class="label">Look</legend>
          <label class="check"><input type="radio" name="look" [checked]="catalog()" (change)="catalog.set(true)" /> Catalog with pictures</label>
          <label class="check"><input type="radio" name="look" [checked]="!catalog()" (change)="catalog.set(false)" /> Plain price list</label>
        </fieldset>
        <fieldset><legend class="label">File</legend>
          <label class="check"><input type="radio" name="fmt" [checked]="format() === 'pdf'" (change)="format.set('pdf')" /> PDF</label>
          <label class="check"><input type="radio" name="fmt" [checked]="format() === 'png'" (change)="format.set('png')" /> Image (PNG)</label>
        </fieldset>
      </div>

      @if (scope() === 'chosen') {
        <div class="search pick-search"><app-icon name="search" [size]="17" />
          <input #q class="input" placeholder="Search products to add" aria-label="Search products to add" (input)="search(q.value)" autocomplete="off" /></div>
        @if (results().length) {
          <ul class="pick">@for (p of results(); track p.id) { <li><button type="button" (click)="add(p); q.value = ''; results.set([])"><strong>{{ p.name }}</strong><span class="muted mono">{{ p.sku }}</span></button></li> }</ul>
        }
        <div class="chips">@for (c of chosen(); track c.id) { <span class="chip">{{ c.name }} <button type="button" (click)="drop(c.id)" [attr.aria-label]="'Remove ' + c.name">×</button></span> }
          @empty { <span class="muted">No items chosen yet — search above, or tick products in the list.</span> }</div>
      }

      <div class="to">
        <div class="field"><label for="cd-c">For customer (optional)</label>
          <input id="cd-c" class="input" list="cd-custs" placeholder="Type a name" [value]="customer()?.name ?? ''" (input)="findCustomer($any($event.target).value)" autocomplete="off" />
          <datalist id="cd-custs">@for (c of customers(); track c.id) { <option [value]="c.name"></option> }</datalist>
          <span class="hint">Their name goes on it, and their email or phone fills in below.</span></div>
        <div class="field"><label for="cd-e">Email to</label><input id="cd-e" class="input" type="email" [value]="emailTo()" (input)="emailTo.set($any($event.target).value)" placeholder="name@example.com" /></div>
        <div class="field"><label for="cd-w">WhatsApp number</label><input id="cd-w" class="input" inputmode="tel" [value]="phone()" (input)="phone.set($any($event.target).value)" placeholder="0803 000 0000" /></div>
      </div>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      <ng-container modal-actions>
        <button type="button" class="btn" [disabled]="busy() || !ready()" (click)="download()"><app-icon name="download" [size]="17" /> Open / download</button>
        <button type="button" class="btn" [disabled]="busy() || !ready()" (click)="share()">Share (WhatsApp…)</button>
        <button type="button" class="btn" [disabled]="busy() || !ready() || !emailTo().trim()" (click)="sendEmail()">Email</button>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !ready() || !phone().trim()" (click)="sendWhatsApp()">Send on WhatsApp</button>
      </ng-container>
    </app-modal>`,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: .8rem 1.2rem; }
    fieldset { border: 0; padding: 0; margin: 0; display: flex; flex-direction: column; gap: .3rem; } .sub { margin-left: 1.4rem; font-size: .85rem; }
    .pick-search { margin: 1rem 0 .4rem; }
    .pick { list-style: none; margin: 0 0 .5rem; padding: 0; border: 1px solid var(--line); border-radius: var(--r-2); max-height: 12rem; overflow: auto; background: #fff; }
    .pick button { display: flex; justify-content: space-between; gap: 1rem; width: 100%; padding: .5rem .9rem; background: none; border: 0; border-bottom: 1px solid var(--line); text-align: left; cursor: pointer; }
    .pick button:hover, .pick button:focus-visible { background: var(--brand-tint); }
    .chips { display: flex; flex-wrap: wrap; gap: .35rem; margin: .3rem 0 .6rem; }
    .chip { display: inline-flex; align-items: center; gap: .3rem; padding: .2rem .3rem .2rem .6rem; border-radius: 99px; background: var(--brand-tint); font-size: .82rem; font-weight: 600; }
    .chip button { border: 0; background: none; cursor: pointer; font-size: 1rem; line-height: 1; padding: 0 .25rem; }
    .to { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: .8rem; margin-top: 1rem; border-top: 1px solid var(--line); padding-top: 1rem; }
  `,
})
export class CatalogDialog {
  private readonly http = inject(HttpClient);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  readonly open = input(false);
  /** Products already ticked on the page; opening the dialog starts from them. */
  readonly preselected = input<ChosenItem[]>([]);
  readonly closed = output<void>();

  protected readonly tiers: { value: Tier; label: string }[] = [
    { value: 'Retailer', label: 'Retail prices' }, { value: 'Wholesaler', label: 'Wholesale prices' }, { value: 'Distributor', label: 'Distributor prices' },
  ];
  protected readonly scope = signal<'all' | 'chosen'>('all');
  protected readonly tier = signal<Tier>('Retailer');
  protected readonly catalog = signal(true);
  protected readonly format = signal<'pdf' | 'png'>('pdf');
  protected readonly inStockOnly = signal(true);
  protected readonly chosen = signal<ChosenItem[]>([]);
  protected readonly results = signal<Product[]>([]);
  protected readonly customers = signal<CustomerLookup[]>([]);
  protected readonly customer = signal<CustomerLookup | null>(null);
  protected readonly emailTo = signal('');
  protected readonly phone = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly ready = computed(() => this.scope() === 'all' || this.chosen().length > 0);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private custTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Each time it opens: start from the page's ticked products (choose-mode when there are some).
    effect(() => {
      if (!this.open()) return;
      const pre = this.preselected();
      untracked(() => { this.chosen.set([...pre]); this.scope.set(pre.length ? 'chosen' : 'all'); this.error.set(''); });
    });
  }

  private request() {
    return {
      customerId: this.customer()?.id ?? null, tier: this.tier(), productIds: this.scope() === 'chosen' ? this.chosen().map(c => c.id) : null,
      catalog: this.catalog(), format: this.format(), includeOutOfStock: !this.inStockOnly(),
    };
  }

  protected search(term: string) {
    if (this.timer) clearTimeout(this.timer);
    const t = term.trim(); if (t.length < 2) { this.results.set([]); return; }
    this.timer = setTimeout(async () => { try { this.results.set((await this.api.products({ search: t, pageSize: 8 })).items); } catch { this.results.set([]); } }, 220);
  }
  protected add(p: Product) { this.chosen.update(xs => (xs.some(x => x.id === p.id) ? xs : [...xs, { id: p.id, name: p.name }])); }
  protected drop(id: number) { this.chosen.update(xs => xs.filter(x => x.id !== id)); }

  protected findCustomer(name: string) {
    const hit = this.customers().find(c => c.name === name);
    if (hit) { this.pickCustomer(hit); return; }
    this.customer.set(null);
    if (this.custTimer) clearTimeout(this.custTimer);
    if (name.trim().length < 2) return;
    this.custTimer = setTimeout(async () => { try { this.customers.set(await this.api.customerLookup(name.trim())); } catch { /* typing still works */ } }, 250);
  }
  private pickCustomer(c: CustomerLookup) {
    this.customer.set(c);
    if (c.customerType === 'Distributor' || c.customerType === 'Wholesaler') this.tier.set(c.customerType);
    else this.tier.set('Retailer');
    if (c.phone && !this.phone()) this.phone.set(c.phone);
  }

  private async file(): Promise<{ blob: Blob; name: string }> {
    const res = await firstValueFrom(this.http.post('/api/catalog/file', this.request(), { observe: 'response', responseType: 'blob' }));
    const cd = res.headers.get('Content-Disposition') ?? '';
    const name = /filename="?([^";]+)"?/.exec(cd)?.[1] ?? (this.format() === 'png' ? 'catalog.png' : 'catalog.pdf');
    return { blob: res.body!, name };
  }

  protected async download() {
    this.busy.set(true); this.error.set('');
    try {
      const { blob, name } = await this.file();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a'); a.href = url; a.download = name; a.target = '_blank'; a.rel = 'noopener'; a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) { this.error.set(await problem(e)); } finally { this.busy.set(false); }
  }

  /** Hands the file to the phone's share sheet (pick WhatsApp there). Where a browser can't share files, it downloads and opens a WhatsApp chat instead. */
  protected async share() {
    this.busy.set(true); this.error.set('');
    try {
      const { blob, name } = await this.file();
      const f = new File([blob], name, { type: blob.type });
      const nav = navigator as Navigator & { canShare?: (d: ShareData) => boolean };
      if (nav.canShare?.({ files: [f] })) { await navigator.share({ files: [f], title: name }); return; }
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a'); a.href = url; a.download = name; a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
      const digits = international(this.phone());
      window.open(`https://wa.me/${digits}?text=${encodeURIComponent('Here is our ' + (this.catalog() ? 'catalog' : 'price list') + ' — the file is attached.')}`, '_blank', 'noopener');
      this.toasts.info('The file was downloaded. Attach it in the WhatsApp chat that just opened.');
    } catch (e) { if ((e as DOMException)?.name !== 'AbortError') this.error.set(await problem(e)); } finally { this.busy.set(false); }
  }

  protected async sendEmail() {
    this.busy.set(true); this.error.set('');
    try {
      const r = await firstValueFrom(this.http.post<{ to: string }>('/api/catalog/email', { request: this.request(), to: this.emailTo().trim(), note: null }));
      this.toasts.ok(`Sent to ${r.to}.`);
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async sendWhatsApp() {
    this.busy.set(true); this.error.set('');
    try {
      const r = await firstValueFrom(this.http.post<{ to: string }>('/api/catalog/whatsapp', { request: this.request(), to: this.phone().trim(), note: null }));
      this.toasts.ok(`Sent on WhatsApp to ${r.to}.`);
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}

function international(phone: string): string {
  const d = phone.replace(/\D/g, '');
  return d.startsWith('234') ? d : d.startsWith('0') ? '234' + d.slice(1) : d;
}

/** A blob error body is JSON problem details; read its title so the person sees the real reason. */
async function problem(e: unknown): Promise<string> {
  const err = e as { error?: unknown };
  if (err?.error instanceof Blob) { try { const j = JSON.parse(await err.error.text()); if (j?.title) return j.detail ? `${j.title} ${j.detail}` : j.title; } catch { /* fall through */ } }
  return messageOf(e);
}
