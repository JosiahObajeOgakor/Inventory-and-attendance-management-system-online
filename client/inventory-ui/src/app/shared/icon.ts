import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

// Hand-drawn 24px stroke icons. Kept in one place so the set stays visually consistent.
const PATHS: Record<string, string> = {
  dashboard: 'M4 4h7v9H4zM13 4h7v5h-7zM13 11h7v9h-7zM4 15h7v5H4z',
  stock: 'M3 20h18M5 20V9l7-5 7 5v11M9 20v-6h6v6',
  product: 'M4 8l8-4 8 4v8l-8 4-8-4zM4 8l8 4 8-4M12 12v8',
  sale: 'M6 3h12v18l-3-2-3 2-3-2-3 2zM9 8h6M9 12h6',
  customer: 'M8 11a3 3 0 100-6 3 3 0 000 6zM2 20c0-3 3-5 6-5s6 2 6 5M16 5a3 3 0 010 6M18 15c2 .6 4 2 4 5',
  supplier: 'M3 7h11v9H3zM14 10h4l3 3v3h-7zM7 19a1.5 1.5 0 100-3 1.5 1.5 0 000 3zM17 19a1.5 1.5 0 100-3 1.5 1.5 0 000 3z',
  purchase: 'M5 4h14l-1.5 9h-11zM5 4L4 2H2M9 18a1 1 0 100 2 1 1 0 000-2zM16 18a1 1 0 100 2 1 1 0 000-2z',
  finance: 'M4 20V10M10 20V4M16 20v-7M22 20H2',
  users: 'M12 12a4 4 0 100-8 4 4 0 000 8zM4 21c0-4 4-6 8-6s8 2 8 6',
  audit: 'M9 4h9v16H6V7zM9 4v3H6M9 11h6M9 15h6',
  search: 'M10.5 17a6.5 6.5 0 100-13 6.5 6.5 0 000 13zM15.5 15.5L20 20',
  plus: 'M12 5v14M5 12h14',
  close: 'M6 6l12 12M18 6L6 18',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  print: 'M7 8V3h10v5M7 17H4v-7h16v7h-3M7 14h10v7H7z',
  logout: 'M10 4H5v16h5M14 8l4 4-4 4M18 12H9',
  alert: 'M12 4l9 16H3zM12 10v4M12 17v.5',
  arrow: 'M5 12h14M13 6l6 6-6 6',
  swap: 'M4 8h13M13 4l4 4-4 4M20 16H7M11 12l-4 4 4 4',
  menu: 'M4 7h16M4 12h16M4 17h16',
  scan: 'M4 8V4h4M16 4h4v4M20 16v4h-4M8 20H4v-4M7 12h10',
  edit: 'M4 20h4L19 9l-4-4L4 16zM13 7l4 4',
  trash: 'M5 7h14M9 7V4h6v3M7 7l1 13h8l1-13',
  chevron: 'M9 6l6 6-6 6',
  download: 'M12 4v11M7 11l5 5 5-5M5 20h14',
  clock: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 7v5l3 2',
  tag: 'M3 12V4h8l10 10-8 8zM8 8h.01',
  file: 'M6 3h9l4 4v14H6zM14 3v5h5M9 13h6M9 17h6',
  cash: 'M3 7h18v10H3zM12 14a2 2 0 100-4 2 2 0 000 4z',
  gift: 'M4 11h16v9H4zM3 7h18v4H3zM12 7v13M12 7c-2-4-6-3-5 0M12 7c2-4 6-3 5 0',
  settings: 'M4 7h9M17 7h3M4 12h3M11 12h9M4 17h11M19 17h1M15 5v4M9 10v4M17 15v4',
  barcode: 'M4 5v14M7 5v14M11 5v14M14 5v14M17 5v14M20 5v14',
  spark: 'M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8zM19 16l.8 2.2L22 19l-2.2.8L19 22l-.8-2.2L16 19l2.2-.8z',
};

@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
      stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path [attr.d]="d()" /></svg>`,
  styles: `:host { display: inline-flex; line-height: 0; }`,
})
export class Icon {
  readonly name = input.required<string>();
  readonly size = input(20);
  protected readonly d = computed(() => PATHS[this.name()] ?? '');
}
