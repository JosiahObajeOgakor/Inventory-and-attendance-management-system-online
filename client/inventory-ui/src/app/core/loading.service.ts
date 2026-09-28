import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal, untracked } from '@angular/core';
import { finalize } from 'rxjs';

/** Mark one request as background: `this.http.get(url, { context: new HttpContext().set(SILENT, true) })`. */
export const SILENT = new HttpContextToken<boolean>(() => false);

/**
 * Calls that happen while the person types or in the background. A full-screen spinner on these would flash on every keystroke
 * or interrupt them, so they never show it: live price previews, pickers' search-as-you-type, advice hints, the assistant
 * (which shows its own "thinking" state), and the health check.
 */
const SILENT_URLS = [/\/api\/sales\/preview/, /\/lookup(\?|$)/, /\/api\/analytics\/(sale|purchase)-advice/, /\/api\/ai\//, /\/api\/health/];

/**
 * The global loading state. The spinner appears only when something has been waiting ~250 ms (fast calls never flicker it),
 * and once shown it stays at least ~400 ms so it doesn't blink on and off.
 */
@Injectable({ providedIn: 'root' })
export class Loading {
  private static readonly SHOW_AFTER_MS = 250;
  private static readonly MIN_VISIBLE_MS = 400;

  private readonly pending = signal(0);
  private readonly shown = signal(false);
  private showTimer: ReturnType<typeof setTimeout> | null = null;
  private hideTimer: ReturnType<typeof setTimeout> | null = null;
  private shownAt = 0;
  private quietDepth = 0;

  /** True while the overlay should be on screen. */
  readonly visible = computed(() => this.shown());
  /** What's being waited for, when a caller named it (e.g. "Signing in"). */
  readonly label = signal<string | null>(null);

  /** Runs `fn` so the requests it STARTS (synchronously) don't show the spinner — for background refreshes. */
  quiet<T>(fn: () => T): T {
    this.quietDepth++;
    try { return fn(); } finally { this.quietDepth--; }
  }

  get isQuiet(): boolean { return this.quietDepth > 0; }

  // begin/end run inside whatever started the request — possibly an effect or a computed — so their signal reads are untracked:
  // otherwise that caller would come to depend on the spinner and re-run every time it shows or hides.
  begin(): void { untracked(() => this.beginNow()); }
  end(): void { untracked(() => this.endNow()); }

  private beginNow(): void {
    this.pending.update(n => n + 1);
    if (this.hideTimer) { clearTimeout(this.hideTimer); this.hideTimer = null; }
    if (!this.shown() && !this.showTimer)
      this.showTimer = setTimeout(() => { this.showTimer = null; if (this.pending() > 0) { this.shown.set(true); this.shownAt = Date.now(); } }, Loading.SHOW_AFTER_MS);
  }

  private endNow(): void {
    this.pending.update(n => Math.max(0, n - 1));
    if (this.pending() > 0) return;
    if (this.showTimer) { clearTimeout(this.showTimer); this.showTimer = null; }
    if (!this.shown()) { this.label.set(null); return; }
    const left = Math.max(0, Loading.MIN_VISIBLE_MS - (Date.now() - this.shownAt));
    this.hideTimer = setTimeout(() => { this.hideTimer = null; if (this.pending() === 0) { this.shown.set(false); this.label.set(null); } }, left);
  }

  /** Shows the spinner (with an optional label) for a piece of work that isn't a single HTTP call. */
  async track<T>(work: Promise<T>, label?: string): Promise<T> {
    if (label) this.label.set(label);
    this.begin();
    try { return await work; } finally { this.end(); }
  }
}

/** Counts every API call towards the global spinner, except the silent ones. */
export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  const loading = inject(Loading);
  const silent = !req.url.startsWith('/api') || req.context.get(SILENT) || loading.isQuiet
    || SILENT_URLS.some(r => r.test(req.urlWithParams))
    || (req.method === 'GET' && req.params.has('search'));   // search-as-you-type in lists and pickers
  if (silent) return next(req);
  loading.begin();
  return next(req).pipe(finalize(() => loading.end()));
};
