import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, viewChild } from '@angular/core';
import { Loading } from '../core/loading.service';

/**
 * The global loading overlay: a centred card over a soft backdrop, shown while an API call is in flight (see Loading).
 * The mark is a "kibble orbit" — a stock-count ring in the business's brand colour with three feed pellets dropping in turn —
 * so it reads as this app, not a generic spinner. It takes the brand colour of whichever business is open, blocks double-clicks
 * while something saves, is announced to screen readers, and calms to a gentle pulse when reduced motion is requested.
 */
@Component({
  selector: 'app-loader',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // A native <dialog> opened with showModal(): it enters the browser's top layer ABOVE any open modal form (those are dialogs too),
  // dims and blocks the page behind it, and can't be dismissed with Esc while work is in flight.
  template: `
    <dialog #dlg class="veil" (cancel)="$event.preventDefault()" aria-label="Loading">
      @if (loading.visible()) {
        <div class="card" role="status" aria-live="polite" aria-busy="true">
          <svg class="mark" viewBox="0 0 64 64" aria-hidden="true">
            <circle class="track" cx="32" cy="32" r="26" />
            <circle class="arc" cx="32" cy="32" r="26" pathLength="100" />
            <g class="kibble">
              <rect class="k k1" x="21" y="28" width="7" height="9" rx="3.5" />
              <rect class="k k2" x="28.5" y="28" width="7" height="9" rx="3.5" />
              <rect class="k k3" x="36" y="28" width="7" height="9" rx="3.5" />
            </g>
          </svg>
          <span class="text">{{ loading.label() ?? 'Loading' }}<span class="dots" aria-hidden="true"><i>.</i><i>.</i><i>.</i></span></span>
        </div>
      }
    </dialog>`,
  styles: `
    .veil { border: 0; padding: 0; margin: auto; background: transparent; overflow: visible; max-width: calc(100vw - 2rem); cursor: progress; outline: none; }
    .veil::backdrop { background: rgba(22, 32, 42, .28); backdrop-filter: blur(2px); -webkit-backdrop-filter: blur(2px); cursor: progress; animation: fade-in .18s ease-out both; }
    .card {
      display: flex; flex-direction: column; align-items: center; gap: .7rem;
      min-width: 9.5rem; padding: 1.35rem 1.6rem 1.1rem; border-radius: 14px;
      background: var(--paper, #fff); color: var(--ink, #16202a);
      box-shadow: 0 18px 50px -12px rgba(22, 32, 42, .45), 0 0 0 1px rgba(22, 32, 42, .06);
      animation: rise .22s cubic-bezier(.2, .8, .2, 1) both;
    }
    .mark { width: 64px; height: 64px; overflow: visible; }
    .track { fill: none; stroke: var(--brand-tint, #dcebe3); stroke-width: 5; }
    .arc {
      fill: none; stroke: var(--brand, #1f6b4f); stroke-width: 5; stroke-linecap: round;
      stroke-dasharray: 28 72; transform-origin: 32px 32px; animation: orbit 1s linear infinite;
    }
    .k { fill: var(--signal, #f2b90f); stroke: var(--signal-ink, #5a4200); stroke-width: 1; transform-box: fill-box; transform-origin: 50% 100%; }
    .k1 { animation: drop 1.1s ease-in-out infinite; }
    .k2 { animation: drop 1.1s ease-in-out .15s infinite; }
    .k3 { animation: drop 1.1s ease-in-out .3s infinite; }
    .text { font: 600 .875rem/1 var(--font-body, system-ui); letter-spacing: .01em; color: var(--ink-3, #33434f); }
    .dots i { font-style: normal; animation: blink 1.2s infinite; opacity: .2; }
    .dots i:nth-child(2) { animation-delay: .2s; } .dots i:nth-child(3) { animation-delay: .4s; }

    @keyframes orbit { to { transform: rotate(360deg); } }
    @keyframes drop {
      0%, 60%, 100% { transform: translateY(0) scaleY(1); }
      20% { transform: translateY(-8px) scaleY(1.05); }
      40% { transform: translateY(0) scaleY(.82); }
    }
    @keyframes blink { 0%, 100% { opacity: .2; } 40% { opacity: 1; } }
    @keyframes fade-in { from { opacity: 0; } }
    @keyframes rise { from { opacity: 0; transform: translateY(6px) scale(.97); } }

    @media (max-width: 480px) { .card { min-width: 8rem; padding: 1.1rem 1.25rem .95rem; } .mark { width: 54px; height: 54px; } }
    @media (prefers-reduced-motion: reduce) {
      .arc { animation: none; stroke-dasharray: none; opacity: .9; }
      .k1, .k2, .k3 { animation: calm 1.6s ease-in-out infinite; }
      .dots i { animation: none; opacity: 1; }
      .veil::backdrop, .card { animation: none; }
      @keyframes calm { 50% { opacity: .35; } }
    }
  `,
})
export class Loader {
  protected readonly loading = inject(Loading);
  private readonly dlg = viewChild.required<ElementRef<HTMLDialogElement>>('dlg');

  constructor() {
    effect(() => {
      const el = this.dlg().nativeElement;
      if (this.loading.visible()) {
        // Re-open so it lands on top of any modal that opened after it; the browser returns focus when it closes.
        if (el.open) el.close();
        el.showModal();
      } else if (el.open) el.close();
    });
  }
}
