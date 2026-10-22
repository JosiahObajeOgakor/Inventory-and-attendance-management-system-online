
import { ChangeDetectionStrategy, Component, Pipe, PipeTransform, computed, input, output } from '@angular/core';

/** ₦1,234.50 — Nigerian naira, two decimals. */
@Pipe({ name: 'naira' })
export class NairaPipe implements PipeTransform {
  private static readonly f = new Intl.NumberFormat('en-NG', { style: 'currency', currency: 'NGN', minimumFractionDigits: 2 });
  transform(v: number | null | undefined): string { return v === null || v === undefined ? '—' : NairaPipe.f.format(v); }
}

/** 12 Sep 2026 from "2026-09-12" or an ISO timestamp (UTC timestamps are shown in the browser's zone). */
@Pipe({ name: 'day' })
export class DayPipe implements PipeTransform {
  transform(v: string | null | undefined): string {
    if (!v) return '—';
    const d = /^\d{4}-\d{2}-\d{2}$/.test(v) ? new Date(v + 'T00:00:00') : new Date(v.endsWith('Z') ? v : v + 'Z');
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }
}

@Pipe({ name: 'stampTime' })
export class StampTimePipe implements PipeTransform {
  transform(v: string | null | undefined): string {
    if (!v) return '—';
    return new Date(v.endsWith('Z') ? v : v + 'Z').toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
  }
}

const KIND: Record<string, string> = {
  Paid: 'ok', Received: 'ok', OK: 'ok', Gold: 'warn', Silver: 'info', Bronze: 'info', Accrued: 'brand',
  Partial: 'warn', Pending: 'warn', 'Low stock': 'warn', 'Expiring soon': 'warn', 'Reorder now': 'warn', Low: 'warn',
  Unpaid: 'bad', Voided: 'bad', Cancelled: 'bad', 'Out of stock': 'bad',
};

/** A status stencilled like a bag stamp. The colour is never the only signal: the word is always there. */
@Component({
  selector: 'app-stamp',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="stamp" [class]="'stamp-' + kind()">{{ label() }}</span>`,
})
export class Stamp {
  readonly label = input.required<string>();
  protected readonly kind = computed(() => KIND[this.label()] ?? 'info');
}

@Component({
  selector: 'app-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (total() > 0) {
      <nav class="pager" aria-label="Pages">
        <span class="muted">{{ from() }}–{{ to() }} of {{ total() }}</span>
        <div class="mid">
          <button type="button" class="btn btn-sm" [disabled]="page() <= 1" (click)="pageChange.emit(1)" aria-label="First page">«</button>
          <button type="button" class="btn btn-sm" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">Previous</button>
          @for (p of pages(); track $index) {
            @if (p === 0) { <span class="gap" aria-hidden="true">…</span> }
            @else { <button type="button" class="btn btn-sm num" [class.on]="p === page()" [attr.aria-current]="p === page() ? 'page' : null" (click)="pageChange.emit(p)">{{ p }}</button> }
          }
          <button type="button" class="btn btn-sm" [disabled]="page() >= last()" (click)="pageChange.emit(page() + 1)">Next</button>
          <button type="button" class="btn btn-sm" [disabled]="page() >= last()" (click)="pageChange.emit(last())" aria-label="Last page">»</button>
        </div>
        @if (sizeable()) { <label class="size">Rows
          <select class="input" [value]="pageSize()" (change)="sizeChange.emit(+$any($event.target).value)" aria-label="Rows per page">
            @for (s of sizes; track s) { <option [value]="s" [selected]="s === pageSize()">{{ s }}</option> }
          </select>
        </label> }
      </nav>
    }`,
  styles: `.pager { display: flex; justify-content: space-between; align-items: center; gap: .6rem; flex-wrap: wrap; padding: .7rem 1.125rem; border-top: 1px solid var(--line); font-size: .8125rem; } .mid { display: flex; gap: .3rem; flex-wrap: wrap; align-items: center; }
    .num { min-width: 2rem; } .num.on { background: var(--brand, #0f1e3d); color: var(--brand-on, #fff); border-color: var(--brand, #0f1e3d); font-weight: 700; } .gap { padding: 0 .2rem; color: var(--muted, #7a8497); }
    .size { display: flex; align-items: center; gap: .4rem; color: var(--muted, #7a8497); } .size select { width: auto; padding: .2rem .4rem; }`,
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly total = input.required<number>();
  readonly pageChange = output<number>();
  /** Offered only by lists that can change their page size; the others ignore it. */
  readonly sizeChange = output<number>();
  readonly sizeable = input(false);
  protected readonly sizes = [10, 15, 25, 50, 100];
  protected readonly from = computed(() => (this.page() - 1) * this.pageSize() + 1);
  protected readonly to = computed(() => Math.min(this.page() * this.pageSize(), this.total()));
  protected readonly last = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
  /** Page numbers to show: first, last, and a window around the current page, with 0 standing for a gap. */
  protected readonly pages = computed(() => {
    const last = this.last(), cur = Math.min(this.page(), last);
    const keep = new Set([1, last, cur - 1, cur, cur + 1].filter(n => n >= 1 && n <= last));
    const out: number[] = []; let prev = 0;
    for (const n of [...keep].sort((a, b) => a - b)) { if (n - prev > 1) out.push(0); out.push(n); prev = n; }
    return out;
  });
}
export const SHARED_PIPES = [NairaPipe, DayPipe, StampTimePipe];
