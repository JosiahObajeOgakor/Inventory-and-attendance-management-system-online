import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { Api2 } from '../core/api-more';
import { messageOf } from '../core/api.service';
import { EmailPreview } from '../core/models-dash';
import { Icon } from './icon';
import { Modal } from './modal';
import { Toasts } from './feedback';

/**
 * Send a sale receipt to the customer, or a purchase order to the supplier, by WhatsApp or email — the PDF goes with it.
 * WhatsApp has two routes: from the business's own number (the server sends it), or "Share" from this phone/computer, which always works
 * (WhatsApp only lets a business number message someone who wrote to it in the last 24 hours).
 */
@Component({
  selector: 'app-send-document',
  imports: [FormsModule, Icon, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-modal [open]="open()" [heading]="heading()" [wide]="true" (closed)="closed.emit()">
      <div class="tabs" role="tablist">
        <button type="button" role="tab" [attr.aria-selected]="tab() === 'whatsapp'" [class.on]="tab() === 'whatsapp'" (click)="tab.set('whatsapp')">WhatsApp</button>
        <button type="button" role="tab" [attr.aria-selected]="tab() === 'email'" [class.on]="tab() === 'email'" (click)="pickEmail()">Email</button>
      </div>

      @if (tab() === 'whatsapp') {
        <div class="form">
          <div class="field"><label for="sd-ph">{{ who() }}’s WhatsApp number</label>
            <input id="sd-ph" class="input" type="tel" inputmode="tel" autocomplete="off" placeholder="0803 000 0000" [ngModel]="phone()" (ngModelChange)="phone.set($event)" /></div>
          <div class="ways">
            <div class="way">
              <strong>Send from the business number</strong>
              <span class="muted">Arrives from {{ waReady() ? 'your business WhatsApp' : 'the business WhatsApp (not set up on the server yet)' }}. WhatsApp only delivers it if they messaged that number in the last 24 hours.</span>
              <button type="button" class="btn btn-primary" [disabled]="busy() || !waReady() || !phoneOk()" (click)="sendWhatsApp()">{{ busy() ? 'Sending…' : 'Send on WhatsApp' }}</button>
            </div>
            <div class="way">
              <strong>Share from this device</strong>
              <span class="muted">Opens WhatsApp here with the PDF, so you send it yourself. Works for anyone, any time.</span>
              <button type="button" class="btn" [disabled]="busy()" (click)="share()"><app-icon name="download" [size]="17" /> Share PDF</button>
            </div>
          </div>
        </div>
      } @else {
        <div class="grid">
          <div class="form">
            <div class="field"><label for="sd-to">Send to</label>
              <input id="sd-to" class="input" type="email" inputmode="email" autocomplete="off" placeholder="name@example.com" [ngModel]="to()" (ngModelChange)="to.set($event)" (blur)="refresh()" /></div>
            <div class="field"><label for="sd-n">Personal note <span class="muted">(optional)</span></label>
              <textarea id="sd-n" class="input" rows="4" maxlength="600" [ngModel]="note()" (ngModelChange)="note.set($event)" (blur)="refresh()"></textarea></div>
            @if (!mailReady()) { <p class="notice">Email isn’t set up on the server yet. You can still download the PDF and send it from your own mail.</p> }
          </div>
          <div class="prev">
            <span class="eyebrow">What they will see</span>
            @if (preview(); as p) {
              <div class="subject"><b>Subject:</b> {{ p.subject }}</div>
              <iframe title="Email preview" sandbox="" [srcdoc]="html()"></iframe>
            } @else { <div class="skeleton" style="height:22rem"></div> }
          </div>
        </div>
      }
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

      <ng-container modal-actions>
        <button type="button" class="btn" (click)="closed.emit()">Close</button>
        <a class="btn" [href]="pdfUrl()" target="_blank" rel="noopener"><app-icon name="download" [size]="17" /> Open PDF</a>
        @if (tab() === 'email') { <button type="button" class="btn btn-primary" [disabled]="busy() || !mailReady() || !to().trim()" (click)="sendEmail()">{{ busy() ? 'Sending…' : 'Send email' }}</button> }
      </ng-container>
    </app-modal>`,
  styles: `
    .tabs { display: flex; gap: .4rem; margin-bottom: 1rem; border-bottom: 1px solid var(--line); }
    .tabs button { background: none; border: 0; border-bottom: 2px solid transparent; padding: .55rem .9rem; font-weight: 600; color: var(--muted, #667); cursor: pointer; }
    .tabs button.on { color: var(--brand); border-bottom-color: var(--brand); }
    .form { display: flex; flex-direction: column; gap: .9rem; }
    .ways { display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: .9rem; }
    .way { display: flex; flex-direction: column; gap: .5rem; align-items: flex-start; padding: .9rem 1rem; border: 1px solid var(--line); border-radius: var(--r-2); }
    .way .muted { font-size: .8125rem; line-height: 1.45; flex: 1; }
    .grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.3fr); gap: 1.25rem; }
    iframe { width: 100%; height: 24rem; border: 1px solid var(--line); border-radius: 10px; background: #efeaee; margin-top: .4rem; } .subject { font-size: .8125rem; margin-top: .4rem; }
    .notice.bad { margin-top: .9rem; }
    @media (max-width: 900px) { .grid { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class SendDocument {
  private readonly api2 = inject(Api2);
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  private readonly san = inject(DomSanitizer);

  readonly open = input(false);
  /** 'receipts' = a sale's receipt to the customer; 'purchases' = a purchase order to the supplier. */
  readonly kind = input<'receipts' | 'purchases' | 'supplies'>('receipts');
  readonly docId = input<number | null>(null);
  readonly number = input('');
  readonly defaultEmail = input('');
  readonly defaultPhone = input('');
  readonly closed = output<void>();

  protected readonly tab = signal<'whatsapp' | 'email'>('whatsapp');
  protected readonly phone = signal('');
  protected readonly to = signal('');
  protected readonly note = signal('');
  protected readonly preview = signal<EmailPreview | null>(null);
  protected readonly html = signal<SafeHtml>('');
  protected readonly mailReady = signal(true);
  protected readonly waReady = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected readonly who = computed(() => (this.kind() === 'receipts' ? 'Customer' : 'Supplier'));
  protected readonly label = computed(() => (this.kind() === 'receipts' ? 'Receipt' : this.kind() === 'supplies' ? 'Purchase' : 'Purchase order'));
  protected readonly heading = computed(() => `Send ${this.label().toLowerCase()} ${this.number()}`);
  protected readonly pdfUrl = computed(() =>
    this.kind() === 'receipts' ? `/api/sales/${this.docId()}/receipt.pdf`
      : this.kind() === 'supplies' ? `/api/supplies/${this.docId()}/pdf`
      : `/api/stock-purchases/${this.docId()}/pdf`);
  protected readonly phoneOk = computed(() => this.phone().replace(/\D/g, '').length >= 10);

  constructor() {
    effect(() => {
      if (!this.open()) return;
      untracked(() => {
        this.phone.set(this.defaultPhone()); this.to.set(this.defaultEmail()); this.note.set(''); this.error.set(''); this.preview.set(null);
        // Start on whichever channel we already have a contact for.
        this.tab.set(!this.defaultPhone() && this.defaultEmail() ? 'email' : 'whatsapp');
        void this.start();
      });
    });
  }

  private async start() {
    try { const s = await this.api2.emailStatus(); this.mailReady.set(s.configured); this.waReady.set(!!s.whatsApp); } catch { /* let the send itself report */ }
    if (this.tab() === 'email') await this.refresh();
  }

  protected pickEmail() { this.tab.set('email'); if (!this.preview()) void this.refresh(); }

  protected async refresh() {
    const id = this.docId(); if (id === null) return;
    this.error.set('');
    try {
      const p = await this.api2.previewDocEmail(this.kind(), id, this.to().trim() || null, this.note().trim() || null);
      this.preview.set(p); this.html.set(this.san.bypassSecurityTrustHtml(p.html));   // sandboxed iframe: no scripts run
      if (!this.to().trim() && p.to) this.to.set(p.to);
    } catch (e) { this.error.set(messageOf(e)); }
  }

  protected async sendEmail() {
    const id = this.docId(); if (id === null) return;
    this.busy.set(true); this.error.set('');
    try {
      const r = await this.api2.sendDocEmail(this.kind(), id, this.to().trim(), this.note().trim() || null);
      this.toasts.ok(`${this.label()} emailed to ${r.to}.`); this.closed.emit();
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async sendWhatsApp() {
    const id = this.docId(); if (id === null) return;
    this.busy.set(true); this.error.set('');
    try {
      const r = await this.api2.sendDocWhatsApp(this.kind(), id, this.phone().trim());
      this.toasts.ok(`${this.label()} sent to ${r.to} on WhatsApp.`); this.closed.emit();
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }

  /**
   * Hands the PDF to the device's share sheet (WhatsApp is on it on a phone). Where the browser can't share files (most desktops), the PDF is
   * downloaded and a WhatsApp chat with the number is opened so it can be attached.
   */
  protected async share() {
    this.busy.set(true); this.error.set('');
    try {
      const blob = await firstValueFrom(this.http.get(this.pdfUrl(), { responseType: 'blob' }));
      const name = `${this.label().replace(/ /g, '-')}-${this.number()}.pdf`;
      const file = new File([blob], name, { type: 'application/pdf' });
      const text = `${this.label()} ${this.number()}`;
      if (navigator.canShare?.({ files: [file] })) {
        try { await navigator.share({ files: [file], title: text, text }); } catch { /* the person closed the share sheet */ }
        return;
      }
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 10_000);
      const digits = this.phone().replace(/\D/g, '');
      const intl = digits.startsWith('234') ? digits : digits.startsWith('0') ? '234' + digits.slice(1) : digits;
      if (intl.length >= 10) window.open(`https://wa.me/${intl}?text=${encodeURIComponent(text + ' — PDF attached.')}`, '_blank', 'noopener');
      this.toasts.ok('PDF downloaded. Attach it in the WhatsApp chat that opened.');
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
