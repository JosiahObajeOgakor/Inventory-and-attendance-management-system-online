import { I18n } from '../../core/i18n';
import { Component, ElementRef, PLATFORM_ID, inject, signal, viewChild } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { RevealDirective } from '../../core/reveal.directive';

interface Review { name: string; place: string | null; rating: number; text: string; on: string; }
interface Partner { id: number; name: string; url: string | null; }

/**
 * Reviews from real customers only: written on this page with the customer's consent, published after staff approve them (first name only).
 * Partner logos appear only for partners who gave permission. Until there is something real to show, the section says so plainly.
 */
@Component({
  selector: 'app-testimonials',
  imports: [RevealDirective],
  templateUrl: './testimonials.html',
  styleUrl: './testimonials.scss',
})
export class Testimonials {
  protected readonly t = inject(I18n).t;
  private readonly http = inject(HttpClient);
  protected readonly reviews = signal<Review[]>([]);
  protected readonly partners = signal<Partner[]>([]);
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('write');
  protected readonly rating = signal(5);
  protected readonly sending = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal('');

  constructor() {
    if (!isPlatformBrowser(inject(PLATFORM_ID))) return;
    this.http.get<Review[]>('/api/webchat/reviews').subscribe({ next: r => this.reviews.set(r), error: () => {} });
    this.http.get<Partner[]>('/api/webchat/partners').subscribe({ next: p => this.partners.set(p), error: () => {} });
  }

  protected stars(n: number): string { return '★'.repeat(n) + '☆'.repeat(5 - n); }
  protected open(): void { this.sent.set(false); this.error.set(''); this.dialog()?.nativeElement.showModal(); }
  protected close(): void { this.dialog()?.nativeElement.close(); }
  protected send(name: string, place: string, text: string, consent: boolean): void {
    if (this.sending()) return;
    this.sending.set(true); this.error.set('');
    this.http.post('/api/webchat/reviews', { name, place: place || null, rating: this.rating(), text, consentToPublish: consent }).subscribe({
      next: () => { this.sending.set(false); this.sent.set(true); },
      error: (e: HttpErrorResponse) => { this.sending.set(false); this.error.set(e.error?.title ?? 'We couldn\'t send your review just now. Please try again.'); },
    });
  }
}