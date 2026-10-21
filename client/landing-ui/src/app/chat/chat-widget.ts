import { Component, DestroyRef, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';

interface ChatBubble {
  role: 'user' | 'assistant';
  text: string;
  checkoutUrl?: string;
  quotationNumber?: string;
}

interface WebChatResponse {
  reply: string;
  replies?: string[];
  checkoutUrl?: string | null;
  quotationNumber?: string | null;
  awaitingPayment?: boolean;
  at?: string;
}

interface WebChatUpdates { messages: string[]; awaitingPayment: boolean; at: string; }

const SESSION_KEY = 'chewypet.chat.session';
/** How often to look for the "payment received" message while the customer has an account to pay into. */
const PAYMENT_CHECK_MS = 10_000;

function sessionId(): string {
  try {
    const existing = localStorage.getItem(SESSION_KEY);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    localStorage.setItem(SESSION_KEY, fresh);
    return fresh;
  } catch {
    return crypto.randomUUID();
  }
}

/**
 * The website chat. It talks to the same order-taking assistant as the WhatsApp number: tell it what you want in English, Pidgin, Igbo or Hausa,
 * it shows a bank account to pay into right here, and the confirmation appears in this window once the money arrives.
 */
@Component({
  selector: 'app-chat-widget',
  imports: [FormsModule],
  templateUrl: './chat-widget.html',
  styleUrl: './chat-widget.scss',
})
export class ChatWidget {
  protected readonly open = signal(false);
  protected readonly sending = signal(false);
  protected readonly draft = signal('');
  protected readonly bubbles = signal<ChatBubble[]>([
    { role: 'assistant', text: "Hi! Tell me what you'd like and how many, for example \"2 adult dog food\". English, Pidgin, Igbo or Hausa are all fine. You can pay into an account I show you right here." },
  ]);

  private readonly scrollAnchor = viewChild<ElementRef<HTMLDivElement>>('scrollAnchor');
  private readonly session = sessionId();
  private readonly http = inject(HttpClient);
  private since = new Date().toISOString();
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      this.bubbles();
      queueMicrotask(() => this.scrollAnchor()?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'end' }));
    });
    inject(DestroyRef).onDestroy(() => this.stopChecking());
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  send(): void {
    const text = this.draft().trim();
    if (!text || this.sending()) return;
    this.draft.set('');
    this.bubbles.update((b) => [...b, { role: 'user', text }]);
    this.sending.set(true);

    this.http.post<WebChatResponse>('/api/webchat/message', { sessionId: this.session, message: text }).subscribe({
      next: (res) => {
        const parts = res.replies?.length ? res.replies : [res.reply];
        this.bubbles.update((b) => [
          ...b,
          ...parts.map((t, i) => ({ role: 'assistant' as const, text: t,
            checkoutUrl: i === parts.length - 1 ? res.checkoutUrl ?? undefined : undefined,
            quotationNumber: i === parts.length - 1 ? res.quotationNumber ?? undefined : undefined })),
        ]);
        if (res.at) this.since = res.at;
        if (res.awaitingPayment) this.startChecking(); else this.stopChecking();
        this.sending.set(false);
      },
      error: () => {
        this.bubbles.update((b) => [...b, { role: 'assistant', text: "Sorry, that didn't go through. Please try again, or message us on WhatsApp instead." }]);
        this.sending.set(false);
      },
    });
  }

  /** While a payment is outstanding, quietly look for the bot's "payment received" message so it appears without the customer doing anything. */
  private startChecking(): void {
    if (this.timer) return;
    this.timer = setInterval(() => {
      this.http.get<WebChatUpdates>('/api/webchat/updates', { params: { sessionId: this.session, since: this.since } }).subscribe({
        next: (u) => {
          if (u.messages.length) this.bubbles.update((b) => [...b, ...u.messages.map((t) => ({ role: 'assistant' as const, text: t }))]);
          this.since = u.at;
          if (!u.awaitingPayment) this.stopChecking();
        },
        error: () => { /* try again on the next tick */ },
      });
    }, PAYMENT_CHECK_MS);
  }

  private stopChecking(): void {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
  }
}
