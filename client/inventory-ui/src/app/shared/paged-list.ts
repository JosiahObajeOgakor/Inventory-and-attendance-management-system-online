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

  constructor(private readonly fetch: (q: PageQuery) => Promise<Paged<T>>, readonly pageSize = 15) {}

  async load(): Promise<void> {
    const mine = ++this.seq;
    this.loading.set(true);
    try {
      const r = await this.fetch({ page: this.page(), pageSize: this.pageSize, search: this.search() });
      if (mine !== this.seq) return;   // a newer request is already in flight
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
}

export const asNumber = (v: unknown): number => (v === '' || v === null || v === undefined ? 0 : Number(v));
