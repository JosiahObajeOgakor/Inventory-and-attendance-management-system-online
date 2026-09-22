import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';

/**
 * The full-screen welcome a warehouse clerk sees after signing in, before the dashboard: a short pre-rendered clip (or, until one is added, a still photo
 * with a slow zoom), the time, and one big "Clock in" button. Drop the finished 5-second clip at public/clerk-welcome.mp4 and a poster frame at
 * public/clerk-welcome.jpg; if either is missing the screen quietly falls back, so nothing breaks while the media is being made.
 */
@Component({
  selector: 'app-clerk-welcome',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="welcome" role="dialog" aria-modal="true" aria-labelledby="w-title">
      @if (!videoFailed()) {
        <video class="bg" autoplay muted playsinline preload="auto" [attr.poster]="imageFailed() ? null : '/clerk-welcome.jpg'" (error)="videoFailed.set(true)">
          <source src="/clerk-welcome.mp4" type="video/mp4" (error)="videoFailed.set(true)" />
        </video>
      } @else if (!imageFailed()) {
        <img class="bg still" [class.zoom]="!reduced" src="/clerk-welcome.jpg" alt="" (error)="imageFailed.set(true)" />
      }
      <div class="shade"></div>

      <section class="panel">
        <span class="eyebrow">{{ greeting() }}</span>
        <h1 id="w-title" class="display">{{ name() }}</h1>
        <div class="clock" aria-live="off"><time class="mono">{{ time() }}</time><span>{{ date() }}</span></div>
        <button #go type="button" class="btn btn-primary big" (click)="clockIn.emit()" [disabled]="busy()" autofocus>{{ busy() ? 'Clocking in…' : 'Clock in' }}</button>
        <button type="button" class="link" (click)="notNow.emit()">Not now</button>
      </section>
    </div>`,
  styles: `
    .welcome { position: fixed; inset: 0; z-index: 200; display: grid; place-items: center; background: linear-gradient(135deg, var(--brand-2), var(--brand)); overflow: hidden; }
    .bg { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; } .still.zoom { animation: drift 24s ease-in-out infinite alternate; }
    @keyframes drift { from { transform: scale(1); } to { transform: scale(1.08) translateY(-1.5%); } }
    .shade { position: absolute; inset: 0; background: radial-gradient(ellipse at 50% 80%, rgb(10 16 22 / .25), rgb(10 16 22 / .72)); }
    .panel { position: relative; text-align: center; color: #fff; padding: 2rem 1.5rem; display: flex; flex-direction: column; align-items: center; gap: .6rem; max-width: 26rem; }
    .eyebrow { color: #ffffffcc; letter-spacing: .14em; } h1 { font-size: clamp(2.6rem, 8vw, 4.4rem); line-height: 1; color: #fff; margin: 0; }
    .clock { display: flex; flex-direction: column; gap: .1rem; margin: .8rem 0 1.4rem; } .clock time { font-size: 2.2rem; letter-spacing: -.04em; } .clock span { color: #ffffffb3; font-size: .95rem; }
    .big { min-height: 3.6rem; padding: 0 3.2rem; font-size: 1.15rem; box-shadow: 0 10px 30px rgb(0 0 0 / .35); animation: beat 2.4s ease-in-out infinite; }
    @keyframes beat { 0% { box-shadow: 0 10px 30px rgb(0 0 0 / .35), 0 0 0 0 rgb(255 255 255 / .55); } 70%, 100% { box-shadow: 0 10px 30px rgb(0 0 0 / .35), 0 0 0 16px rgb(255 255 255 / 0); } }
    .link { background: none; border: 0; color: #ffffffcc; text-decoration: underline; cursor: pointer; font: inherit; margin-top: .6rem; padding: .5rem; } .link:hover { color: #fff; }
    @media (prefers-reduced-motion: reduce) { .big, .still.zoom { animation: none; } }
  `,
})
export class ClerkWelcome implements OnInit {
  readonly name = input.required<string>();
  readonly busy = input(false);
  readonly clockIn = output<void>();
  readonly notNow = output<void>();

  protected readonly reduced = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
  protected readonly videoFailed = signal(this.reduced);   // people who ask for less motion get the still photo
  protected readonly imageFailed = signal(false);
  private readonly now = signal(new Date());
  protected readonly time = computed(() => this.now().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }));
  protected readonly date = computed(() => this.now().toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' }));
  protected readonly greeting = computed(() => { const h = this.now().getHours(); return h < 12 ? 'Good morning' : h < 17 ? 'Good afternoon' : 'Good evening'; });
  private readonly destroy = inject(DestroyRef);

  ngOnInit() {
    const t = setInterval(() => this.now.set(new Date()), 15_000);
    this.destroy.onDestroy(() => clearInterval(t));
  }
}
