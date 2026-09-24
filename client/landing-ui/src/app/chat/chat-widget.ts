import { Component, ElementRef, signal, viewChild, effect } from '@angular/core';
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
  checkoutUrl?: string | null;
  quotationNumber?: string | null;
}

const SESSION_KEY = 'chewypet.chat.session';

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
    { role: 'assistant', text: "Hi! I'm the Chewy Pet assistant — ask me about a product, get a quote, or order right here. What's up?" },
  ]);

  private readonly scrollAnchor = viewChild<ElementRef<HTMLDivElement>>('scrollAnchor');
  private readonly session = sessionId();

  constructor(private readonly http: HttpClient) {
    effect(() => {
      this.bubbles();
      queueMicrotask(() => this.scrollAnchor()?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'end' }));
    });
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
        this.bubbles.update((b) => [
          ...b,
          { role: 'assistant', text: res.reply, checkoutUrl: res.checkoutUrl ?? undefined, quotationNumber: res.quotationNumber ?? undefined },
        ]);
        this.sending.set(false);
      },
      error: () => {
        this.bubbles.update((b) => [...b, { role: 'assistant', text: "Sorry, that didn't go through. Please try again, or message us on WhatsApp instead." }]);
        this.sending.set(false);
      },
    });
  }
}
