import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, inject, input, signal } from '@angular/core';

export const compact = (v: number): string => {
  const a = Math.abs(v);
  const s = a >= 1e9 ? (a / 1e9).toFixed(1) + 'B' : a >= 1e6 ? (a / 1e6).toFixed(1) + 'M' : a >= 1e3 ? (a / 1e3).toFixed(a >= 1e4 ? 0 : 1) + 'K' : a.toFixed(0);
  return (v < 0 ? '−₦' : '₦') + s.replace('.0', '');
};
export const naira = (v: number): string => (v < 0 ? '−' : '') + '₦' + Math.abs(v).toLocaleString('en-NG', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export interface Series { key: string; label: string; color: string; values: number[]; }

/**
 * A line/area chart drawn as plain SVG: no chart library, so nothing to load and nothing to hide from a screen reader.
 * The whole plot is one <svg> sized to its container; hovering or focusing a day shows a crosshair and the exact figures.
 */
@Component({
  selector: 'app-line-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap" (mouseleave)="hover.set(-1)">
      <svg [attr.viewBox]="'0 0 ' + w() + ' ' + h" [attr.width]="w()" [attr.height]="h" role="img" [attr.aria-label]="ariaLabel()"
           (mousemove)="move($event)" (touchmove)="move($any($event).touches[0])">
        @for (t of ticks(); track t.y) {
          <line [attr.x1]="padL" [attr.x2]="w() - padR" [attr.y1]="t.y" [attr.y2]="t.y" class="grid" />
          <text [attr.x]="padL - 8" [attr.y]="t.y + 4" text-anchor="end" class="axis">{{ t.label }}</text>
        }
        @for (s of paths(); track s.key) {
          <defs><linearGradient [attr.id]="'g-' + s.key" x1="0" x2="0" y1="0" y2="1"><stop offset="0" [attr.stop-color]="s.color" stop-opacity=".22" /><stop offset="1" [attr.stop-color]="s.color" stop-opacity="0" /></linearGradient></defs>
          <path [attr.d]="s.area" [attr.fill]="'url(#g-' + s.key + ')'" />
          <path [attr.d]="s.line" fill="none" [attr.stroke]="s.color" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" class="ln" />
        }
        @for (l of xLabels(); track l.x) { <text [attr.x]="l.x" [attr.y]="h - 6" text-anchor="middle" class="axis">{{ l.text }}</text> }
        @if (hover() >= 0) {
          <line [attr.x1]="xAt(hover())" [attr.x2]="xAt(hover())" [attr.y1]="padT" [attr.y2]="h - padB" class="cross" />
          @for (s of series(); track s.key) { <circle [attr.cx]="xAt(hover())" [attr.cy]="yAt(s.values[hover()])" r="4.5" [attr.fill]="s.color" stroke="#fff" stroke-width="2" /> }
        }
      </svg>
      @if (hover() >= 0) {
        <div class="tip" [style.left.px]="tipLeft()" role="status">
          <strong>{{ labels()[hover()] }}</strong>
          @for (s of series(); track s.key) { <div><i [style.background]="s.color"></i>{{ s.label }} <b>{{ fmt(s.values[hover()]) }}</b></div> }
        </div>
      }
    </div>`,
  styles: `
    :host { display: block; } .wrap { position: relative; } svg { display: block; overflow: visible; touch-action: pan-y; }
    .grid { stroke: #e9edf3; stroke-width: 1; } .axis { font: 11px var(--font-body); fill: #8b95a7; } .cross { stroke: #c5cddb; stroke-dasharray: 3 3; }
    .ln { stroke-dasharray: 2400; animation: draw 1s ease-out both; } @keyframes draw { from { stroke-dashoffset: 2400; } to { stroke-dashoffset: 0; } }
    .tip { position: absolute; top: 0; transform: translateX(-50%); background: #1c2130; color: #fff; padding: .5rem .7rem; border-radius: 10px; font-size: .75rem; pointer-events: none; white-space: nowrap; box-shadow: 0 8px 24px rgb(20 30 60 / .25); }
    .tip strong { display: block; margin-bottom: .2rem; } .tip i { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: .4rem; } .tip b { font-family: var(--font-mono); margin-left: .3rem; }
    @media (prefers-reduced-motion: reduce) { .ln { animation: none; } }
  `,
})
export class LineChart {
  readonly series = input.required<Series[]>();
  readonly labels = input.required<string[]>();
  protected readonly h = 250; protected readonly padL = 52; protected readonly padR = 12; protected readonly padT = 12; protected readonly padB = 26;
  protected readonly w = signal(640);
  protected readonly hover = signal(-1);
  private readonly host = inject(ElementRef<HTMLElement>);

  constructor() {
    afterNextRender(() => {
      const el = this.host.nativeElement as HTMLElement;
      const set = () => this.w.set(Math.max(320, Math.floor(el.clientWidth)));
      set();
      new ResizeObserver(set).observe(el);
    });
  }

  private readonly max = computed(() => {
    const m = Math.max(1, ...this.series().flatMap(s => s.values));
    const p = Math.pow(10, Math.floor(Math.log10(m))); return Math.ceil(m / p * 1.05 / 0.5) * 0.5 * p;
  });
  protected xAt(i: number) { const n = Math.max(1, this.labels().length - 1); return this.padL + (this.w() - this.padL - this.padR) * (i / n); }
  protected yAt(v: number) { return this.h - this.padB - (this.h - this.padT - this.padB) * (Math.max(0, v) / this.max()); }
  protected readonly ticks = computed(() => [0, .25, .5, .75, 1].map(f => ({ y: this.yAt(this.max() * f), label: compact(this.max() * f) })));
  protected readonly paths = computed(() => this.series().map(s => {
    const pts = s.values.map((v, i) => [this.xAt(i), this.yAt(v)] as const);
    const line = pts.map(([x, y], i) => (i ? 'L' : 'M') + x.toFixed(1) + ' ' + y.toFixed(1)).join(' ');
    const area = pts.length ? `${line} L${pts[pts.length - 1][0].toFixed(1)} ${this.h - this.padB} L${pts[0][0].toFixed(1)} ${this.h - this.padB} Z` : '';
    return { key: s.key, color: s.color, line, area };
  }));
  protected readonly xLabels = computed(() => {
    const n = this.labels().length; const every = Math.max(1, Math.ceil(n / Math.max(2, Math.floor(this.w() / 70))));
    return this.labels().map((text, i) => ({ text, x: this.xAt(i), i })).filter(l => l.i % every === 0);
  });
  protected readonly tipLeft = computed(() => Math.min(this.w() - 90, Math.max(90, this.xAt(this.hover()))));
  protected readonly ariaLabel = computed(() => this.series().map(s => `${s.label} from ${compact(s.values[0] ?? 0)} to ${compact(s.values[s.values.length - 1] ?? 0)}`).join('; '));
  protected fmt(v: number) { return naira(v); }

  protected move(e: { clientX: number }) {
    const box = (this.host.nativeElement as HTMLElement).querySelector('svg')!.getBoundingClientRect();
    const n = this.labels().length; if (!n) return;
    const f = (e.clientX - box.left - this.padL) / (this.w() - this.padL - this.padR);
    this.hover.set(Math.min(n - 1, Math.max(0, Math.round(f * (n - 1)))));
  }
}

/** Ten dots per column, filled in proportion to a value: the picture-at-a-glance the customer cards use. */
@Component({
  selector: 'app-dots',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="col" role="img" [attr.aria-label]="label() + ': ' + text()">
    @for (d of dots(); track $index) { <span [class.on]="d" [style.--c]="color()"></span> }</div>`,
  styles: `
    .col { display: flex; flex-direction: column; gap: 6px; } span { width: 11px; height: 11px; border-radius: 50%; background: #edf0f5; transition: background .3s; } span.on { background: var(--c, #1fbf6b); }
  `,
})
export class Dots {
  readonly fraction = input.required<number>();
  readonly label = input('');
  readonly text = input('');
  readonly color = input('#1fbf6b');
  protected readonly dots = computed(() => { const n = Math.round(Math.max(0, Math.min(1, this.fraction())) * 10); return Array.from({ length: 10 }, (_, i) => i >= 10 - n); });
}

/** Circles sized by share, like the "Users activities" bubbles: bigger circle, bigger slice of stock value. */
@Component({
  selector: 'app-bubbles',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stage" role="img" [attr.aria-label]="aria()">
      @for (b of layout(); track b.name) {
        <div class="bub" [style.width.px]="b.d" [style.height.px]="b.d" [style.left.px]="b.x" [style.top.px]="b.y" [style.background]="b.color" [style.z-index]="b.z" [title]="b.name + ' — ' + b.pct + '%'">
          <span [style.font-size.px]="Math.max(13, b.d / 4)">{{ b.pct }}%</span>
        </div>
      }
    </div>
    <div class="legend">@for (b of layout(); track b.name) { <span><i [style.background]="b.color"></i>{{ b.name }}</span> }</div>`,
  styles: `
    .stage { position: relative; height: 210px; max-width: 320px; margin: 0 auto; } .bub { position: absolute; border-radius: 50%; display: grid; place-items: center; color: #fff; font-family: var(--font-display); font-weight: 700; transition: all .5s; }
    .legend { display: flex; gap: 1rem; justify-content: center; flex-wrap: wrap; margin-top: .6rem; font-size: .8125rem; color: var(--muted); } .legend i { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: .35rem; }
  `,
})
export class Bubbles {
  protected readonly Math = Math;
  readonly items = input.required<{ name: string; value: number }[]>();
  private static readonly COLORS = ['#4db8ff', '#22d3b8', '#1c2130', '#7c8cff'];
  protected readonly layout = computed(() => {
    const items = this.items(); const total = items.reduce((sum, i) => sum + Math.max(0, i.value), 0);
    const sorted = [...items].sort((x, y) => y.value - x.value);
    const raw = sorted.map(it => 70 + 150 * Math.sqrt(total > 0 ? it.value / total : 1 / items.length));
    // Circles sit side by side, each overlapping the last by a fifth; everything is scaled to fit the 300px stage.
    const span = raw.reduce((sum, d, i) => sum + (i < raw.length - 1 ? d * 0.8 : d), 0);
    const k = Math.min(1, 300 / Math.max(span, 1));
    let x = 0;
    return sorted.map((it, i) => {
      const d = Math.round(raw[i] * k); const r = { name: it.name, pct: Math.round(total > 0 ? it.value / total * 100 : 100 / items.length), d, x: Math.round(x),
        y: Math.round((i % 2 === 0 ? 0.45 : 0.62) * (210 - d)), color: Bubbles.COLORS[i % 4], z: 4 - i };
      x += d * 0.8; return r;
    });
  });
  protected readonly aria = computed(() => this.layout().map(b => `${b.name} ${b.pct}%`).join(", "));
}
