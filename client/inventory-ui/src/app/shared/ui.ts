
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
      <div class="pager">
        <span class="muted">{{ from() }}–{{ to() }} of {{ total() }}</span>
        <div>
          <button type="button" class="btn btn-sm" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">Previous</button>
          <button type="button" class="btn btn-sm" [disabled]="to() >= total()" (click)="pageChange.emit(page() + 1)">Next</button>
        </div>
      </div>
    }`,
  styles: `.pager { display: flex; justify-content: space-between; align-items: center; padding: .7rem 1.125rem; border-top: 1px solid var(--line); font-size: .8125rem; } .pager div { display: flex; gap: .4rem; }`,
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly total = input.required<number>();
  readonly pageChange = output<number>();
  protected readonly from = computed(() => (this.page() - 1) * this.pageSize() + 1);
  protected readonly to = computed(() => Math.min(this.page() * this.pageSize(), this.total()));
}

export const SHARED_PIPES = [NairaPipe, DayPipe, StampTimePipe];
