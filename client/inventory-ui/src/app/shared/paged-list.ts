import { signal } from '@angular/core';
import { messageOf } from '../core/api.service';
import { PageQuery, Paged } from '../core/models';

/** Server-side paging + debounced search for a list screen. */
export class PagedList<T> {
  readonly items = signal<T[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly search = signal('');
  readonly loading = signal(true);
  readonly error = signal('');
  private timer: ReturnType<typeof setTimeout> | null = null;
  private seq = 0;

  private readonly size = signal(15);

  constructor(private readonly fetch: (q: PageQuery) => Promise<Paged<T>>, initialSize = 15) { this.size.set(initialSize); }

  /** How many rows a page shows; the person can change it under the list. */
  get pageSize(): number { return this.size(); }

  async load(): Promise<void> {
    const mine = ++this.seq;
    this.loading.set(true);
    try {
      const r = await this.fetch({ page: this.page(), pageSize: this.pageSize, search: this.search() });
      if (mine !== this.seq) return;   // a newer request is already in flight
      // Rows were removed (or a search narrowed the list) and this page no longer exists: show the last one instead of an empty page.
      if (r.items.length === 0 && r.total > 0 && this.page() > 1) { this.page.set(Math.ceil(r.total / this.pageSize)); return await this.load(); }
      this.items.set(r.items); this.total.set(r.total); this.error.set('');
    } catch (e) {
      if (mine === this.seq) this.error.set(messageOf(e));
    } finally { if (mine === this.seq) this.loading.set(false); }
  }

  setSearch(text: string): void {
    this.search.set(text);
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.page.set(1); void this.load(); }, 300);
  }

  goTo(p: number): void { this.page.set(p); void this.load(); }

  setSize(n: number): void { this.size.set(n); this.page.set(1); void this.load(); }
}

export const asNumber = (v: unknown): number => (v === '' || v === null || v === undefined ? 0 : Number(v));
