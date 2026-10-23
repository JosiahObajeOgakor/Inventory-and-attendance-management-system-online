import { Component, ElementRef, afterNextRender, computed, inject, signal, viewChild } from '@angular/core';
import { ChatWidget } from './chat/chat-widget';
import { Nutrition } from './sections/nutrition/nutrition';
import { ProductList } from './sections/product-list/product-list';
import { StoresSection } from './sections/stores/stores';
import { ServiceInfo } from './sections/service-info/service-info';
import { Articles } from './sections/articles/articles';
import { Track } from './core/track';
import { I18n, LANGS } from './core/i18n';
import { Stores } from './core/stores';
import { Team } from './sections/team/team';
import { Testimonials } from './sections/testimonials/testimonials';
import { Trade } from './sections/trade/trade';
import { RevealDirective } from './core/reveal.directive';
import { whatsappDisplay, whatsappHref } from './core/whatsapp';

const BACKYARD_SRC = '/backyard.mp4';

@Component({
  selector: 'app-root',
  imports: [ChatWidget, Nutrition, ProductList, StoresSection, ServiceInfo, Articles, Team, Testimonials, Trade, RevealDirective],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly whatsappHref = whatsappHref("Hi Chewy Pet! I'd like to order some dog food.");
  protected readonly crateWhatsappHref = whatsappHref("Hi Chewy Pet! I'd like to order a collapsible cage.");
  protected readonly whatsappDisplay = whatsappDisplay();
  protected readonly year = new Date().getFullYear();
  private readonly stores = inject(Stores);
  protected readonly i18n = inject(I18n);
  protected readonly t = this.i18n.t;
  protected readonly langs = LANGS;
  protected readonly main = computed(() => this.stores.find('chewypets') ?? null);

  private readonly heroVideo = viewChild.required<ElementRef<HTMLVideoElement>>('heroVideo');
  private readonly backyard = viewChild.required<ElementRef<HTMLVideoElement>>('backyard');
  private heroInView = true;
  /** WCAG 2.2.2: moving background video can be paused (and stays paused for both clips). */
  protected readonly videoPaused = signal(false);
  protected toggleVideo(): void { this.videoPaused.update(p => !p); this.sync(); }
  private readonly openWindows = new Set<Element>();

  constructor() {
    const track = inject(Track);
    afterNextRender(() => {
      track.event('site_view', 'chewypets', undefined, true);
      this.sync();
      if (!('IntersectionObserver' in window)) return;

      new IntersectionObserver(([entry]) => {
        this.heroInView = entry.isIntersecting;
        this.sync();
      }).observe(this.heroVideo().nativeElement);

      // The backyard clip isn't fetched at all until the first window is close.
      const observer = new IntersectionObserver(
        (entries) => {
          for (const entry of entries) {
            if (entry.isIntersecting) this.openWindows.add(entry.target);
            else this.openWindows.delete(entry.target);
          }
          const backyard = this.backyard().nativeElement;
          if (this.openWindows.size > 0 && !backyard.getAttribute('src')) backyard.src = BACKYARD_SRC;
          this.sync();
        },
        { rootMargin: '300px 0px' },
      );
      document.querySelectorAll('.window').forEach((w) => observer.observe(w));

      // Browsers pause silent video in background tabs; pick up where we left off when
      // someone comes back (e.g. from WhatsApp).
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') this.sync();
      });
    });
  }

  // Both videos autoplay; each is only decoded while it's actually on screen.
  private sync(): void {
    const on = !this.videoPaused();
    this.setPlaying(this.heroVideo().nativeElement, on && this.heroInView);
    const backyard = this.backyard().nativeElement;
    this.setPlaying(backyard, on && this.openWindows.size > 0 && !!backyard.getAttribute('src'));
  }

  private setPlaying(video: HTMLVideoElement, shouldPlay: boolean): void {
    if (shouldPlay) video.play().catch(() => {});
    else video.pause();
  }
}
