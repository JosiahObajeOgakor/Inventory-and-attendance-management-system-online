import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { AiStatus, ChatTurn } from '../../core/models-dash';
import { Auth } from '../../core/auth.service';
import { Icon } from '../../shared/icon';

interface Msg extends ChatTurn { kind?: 'error' | 'limit'; }

const SUGGESTIONS = [
  'How did sales and profit go this month compared with last month?',
  'Which customers deserve credit, a bonus or a higher rebate?',
  'Which products are likely to run out, and when?',
  'What could turn into a loss if I do nothing?',
  'Which products are not selling and how do I move them?',
];

/** A chat box for the owner. It answers only from this business's records, and is limited to ten questions per rolling 24 hours. */
@Component({
  selector: 'app-assistant',
  imports: [FormsModule, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (auth.isAdmin()) {
      <button type="button" class="fab" (click)="toggle()" [attr.aria-expanded]="open()" aria-controls="ai-panel" aria-label="Ask the assistant">
        <app-icon [name]="open() ? 'close' : 'spark'" [size]="22" />
      </button>
      @if (open()) {
        <section id="ai-panel" class="panel" role="dialog" aria-label="Assistant" aria-modal="false">
          <header>
            <div><strong>Ask about your business</strong><span>Answers come only from {{ auth.companyName() }}’s records</span></div>
            @if (status(); as s) { <span class="quota" [class.out]="s.remaining === 0" title="Questions left in the last 24 hours">{{ s.remaining }}/{{ s.dailyLimit }} left</span> }
          </header>

          <div class="log" #log aria-live="polite">
            @if (!msgs().length) {
              <p class="hello">Ask about sales, profit, stock, debts, customers or what could go wrong. I can’t change anything — I only read.</p>
              <div class="chips">@for (s of suggestions; track s) { <button type="button" (click)="ask(s)" [disabled]="busy() || out()">{{ s }}</button> }</div>
            }
            @for (m of msgs(); track $index) {
              <div class="msg" [class.me]="m.role === 'user'" [class.err]="m.kind === 'error'" [class.lim]="m.kind === 'limit'">{{ m.text }}</div>
            }
            @if (busy()) { <div class="msg typing" aria-label="Thinking"><i></i><i></i><i></i></div> }
          </div>

          @if (status() && !status()!.configured) {
            <p class="foot warn">The assistant isn’t switched on: the server has no OpenAI key yet.</p>
          } @else if (out()) {
            <p class="foot warn">You’ve used today’s questions. {{ next() }}</p>
          } @else {
            <form class="in" (ngSubmit)="send()">
              <textarea rows="2" maxlength="500" placeholder="Ask a question…" aria-label="Your question" [(ngModel)]="draft" name="q" (keydown.enter)="onEnter($event)" [disabled]="busy()"></textarea>
              <button type="submit" class="btn btn-primary" [disabled]="busy() || !draft.trim()">Ask</button>
            </form>
            <p class="foot">{{ draft.length }}/500 · Enter to send</p>
          }
        </section>
      }
    }`,
  styles: `
    .fab { position: fixed; right: 1.25rem; bottom: 1.25rem; z-index: 60; width: 56px; height: 56px; border-radius: 50%; border: 0; background: #1c2130; color: #fff; display: grid; place-items: center; cursor: pointer; box-shadow: 0 10px 28px rgb(20 30 60 / .35); }
    .fab:hover { background: #3db4ff; } .fab:focus-visible { outline: 3px solid #3db4ff; outline-offset: 3px; }
    .panel { position: fixed; right: 1.25rem; bottom: 5.5rem; z-index: 60; width: min(26rem, calc(100vw - 2rem)); height: min(36rem, calc(100dvh - 8rem)); background: #fff; border-radius: 18px; box-shadow: 0 20px 60px rgb(20 30 60 / .3); display: flex; flex-direction: column; overflow: hidden; animation: up .18s ease-out; }
    @keyframes up { from { opacity: 0; transform: translateY(10px); } }
    header { display: flex; justify-content: space-between; align-items: center; gap: .6rem; padding: .9rem 1rem; background: #1c2130; color: #fff; } header strong { display: block; font-size: .95rem; } header span { font-size: .72rem; color: #a9b4ca; }
    .quota { background: #ffffff1f; padding: .25rem .6rem; border-radius: 99px; color: #fff; white-space: nowrap; } .quota.out { background: #ef5b52; }
    .log { flex: 1; overflow: auto; padding: 1rem; display: flex; flex-direction: column; gap: .6rem; background: #f6f8fb; }
    .hello { margin: 0; font-size: .84rem; color: #5b6579; line-height: 1.5; } .chips { display: flex; flex-direction: column; gap: .4rem; } .chips button { text-align: left; border: 1px solid #dfe5ef; background: #fff; border-radius: 12px; padding: .55rem .7rem; font-size: .8125rem; color: #1c2130; cursor: pointer; } .chips button:hover:not(:disabled) { border-color: #3db4ff; background: #f3f9ff; }
    .msg { max-width: 88%; padding: .6rem .8rem; border-radius: 14px; background: #fff; color: #1c2130; font-size: .84rem; line-height: 1.5; white-space: pre-wrap; align-self: flex-start; box-shadow: 0 1px 2px #0001; }
    .msg.me { align-self: flex-end; background: #3db4ff; color: #fff; } .msg.err { background: #fdeceb; color: #7c1f18; } .msg.lim { background: #fff3dc; color: #6b4600; }
    .typing { display: flex; gap: 4px; padding: .8rem; } .typing i { width: 7px; height: 7px; border-radius: 50%; background: #b5bdcc; animation: b 1s infinite; } .typing i:nth-child(2) { animation-delay: .15s; } .typing i:nth-child(3) { animation-delay: .3s; } @keyframes b { 40% { transform: translateY(-4px); background: #3db4ff; } }
    .in { display: flex; gap: .5rem; padding: .7rem .8rem 0; align-items: flex-end; } .in textarea { flex: 1; resize: none; border: 1px solid #dfe5ef; border-radius: 12px; padding: .55rem .7rem; font: .84rem var(--font-body); } .in textarea:focus { outline: 2px solid #3db4ff; border-color: transparent; }
    .foot { margin: 0; padding: .35rem .9rem .7rem; font-size: .7rem; color: #8b95a7; } .foot.warn { color: #8a5a00; background: #fff8e8; padding: .8rem 1rem; font-size: .8125rem; }
    @media (prefers-reduced-motion: reduce) { .panel, .typing i { animation: none; } }
  `,
})
export class AssistantPanel {
  private readonly api = inject(Api2);
  protected readonly auth = inject(Auth);
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');
  protected readonly suggestions = SUGGESTIONS;
  protected readonly open = signal(false);
  protected readonly busy = signal(false);
  protected readonly msgs = signal<Msg[]>([]);
  protected readonly status = signal<AiStatus | null>(null);
  protected draft = '';
  protected readonly out = computed(() => !!this.status() && this.status()!.configured && this.status()!.remaining === 0);
  protected readonly next = computed(() => {
    const at = this.status()?.nextAvailableUtc; if (!at) return '';
    const d = new Date(at.endsWith('Z') ? at : at + 'Z');
    const tomorrow = d.toDateString() !== new Date().toDateString();
    return `Come back ${tomorrow ? 'tomorrow' : 'today'} after ${d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}.`;
  });

  protected async toggle() {
    this.open.update(v => !v);
    if (this.open()) { try { this.status.set(await this.api.aiStatus()); } catch { /* the panel still opens */ } }
  }

  protected onEnter(e: Event) { const k = e as KeyboardEvent; if (!k.shiftKey) { k.preventDefault(); void this.send(); } }
  protected send() { const q = this.draft.trim(); if (q) void this.ask(q); }

  protected async ask(question: string) {
    if (this.busy()) return;
    const history: ChatTurn[] = this.msgs().filter(m => !m.kind).map(m => ({ role: m.role, text: m.text })).slice(-6);
    this.msgs.update(m => [...m, { role: 'user', text: question }]); this.draft = ''; this.busy.set(true); this.scroll();
    try {
      const r = await this.api.aiAsk(question, history);
      this.msgs.update(m => [...m, { role: 'assistant', text: r.answer, kind: r.limited ? 'limit' : undefined }]);
      this.status.set({ configured: true, remaining: r.remaining, dailyLimit: r.dailyLimit, nextAvailableUtc: r.nextAvailableUtc });
    } catch (e) { this.msgs.update(m => [...m, { role: 'assistant', text: messageOf(e), kind: 'error' }]); }
    finally { this.busy.set(false); this.scroll(); }
  }

  private scroll() { setTimeout(() => { const el = this.log()?.nativeElement; if (el) el.scrollTop = el.scrollHeight; }); }
}
