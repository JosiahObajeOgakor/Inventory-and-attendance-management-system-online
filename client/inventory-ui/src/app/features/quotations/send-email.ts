import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { CustomerLookup } from '../../core/models';
import { EmailPreview } from '../../core/models-dash';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';

/**
 * Send a quotation or the price list to a customer: shows exactly the email that will go out, lets you add a personal note,
 * and either sends it from the business's mailbox with the PDF attached or lets you download the PDF and send it yourself.
 */
@Component({
  selector: 'app-send-email',
  imports: [FormsModule, Icon, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-modal [open]="open()" [heading]="mode() === 'quotation' ? 'Email this quotation' : 'Email the price list'" [wide]="true" (closed)="closed.emit()">
      <div class="grid">
        <div class="form">
          @if (mode() === 'price-list') {
            <div class="field"><label for="se-c">Customer</label>
              <select id="se-c" class="input" [ngModel]="customerId()" (ngModelChange)="pickCustomer($event)">
                <option [ngValue]="null">Nobody in particular</option>
                @for (c of customers(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }
              </select></div>
            <div class="field"><label for="se-t">Price list</label>
              <select id="se-t" class="input" [ngModel]="tier()" (ngModelChange)="tier.set($event); refresh()"><option value="">Match the customer</option><option>Retailer</option><option>Wholesaler</option><option>Distributor</option></select></div>
          }
          <div class="field"><label for="se-to">Send to</label>
            <input id="se-to" class="input" type="email" inputmode="email" autocomplete="off" placeholder="customer@example.com" [ngModel]="to()" (ngModelChange)="to.set($event)" (blur)="refresh()" /></div>
          <div class="field"><label for="se-n">Personal note <span class="muted">(optional)</span></label>
            <textarea id="se-n" class="input" rows="4" maxlength="600" [ngModel]="note()" (ngModelChange)="note.set($event)" (blur)="refresh()" placeholder="e.g. Delivery is free within Lekki this week."></textarea></div>
          @if (!configured()) { <p class="notice">Email isn’t set up on the server yet. You can still download the PDF and send it from your own mail.</p> }
          @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
        </div>
        <div class="prev">
          <span class="eyebrow">What the customer will see</span>
          @if (preview(); as p) {
            <div class="subject"><b>Subject:</b> {{ p.subject }}</div>
            <iframe title="Email preview" sandbox="" [srcdoc]="html()"></iframe>
          } @else { <div class="skeleton" style="height:26rem"></div> }
        </div>
      </div>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <a class="btn" [href]="pdfUrl()" target="_blank" rel="noopener"><app-icon name="download" [size]="17" /> Download PDF</a>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !configured() || !to().trim()" (click)="send()">{{ busy() ? 'Sending…' : 'Send email' }}</button>
      </ng-container>
    </app-modal>`,
  styles: `
    .grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.3fr); gap: 1.25rem; } .form { display: flex; flex-direction: column; gap: .9rem; }
    iframe { width: 100%; height: 30rem; border: 1px solid var(--line); border-radius: 10px; background: #efeaee; margin-top: .4rem; } .subject { font-size: .8125rem; margin-top: .4rem; }
    @media (max-width: 900px) { .grid { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class SendEmail {
  private readonly api = inject(Api);
  private readonly api2 = inject(Api2);
  private readonly toasts = inject(Toasts);
  private readonly san = inject(DomSanitizer);

  readonly open = input(false);
  readonly mode = input<'quotation' | 'price-list'>('quotation');
  readonly quotationId = input<number | null>(null);
  readonly defaultTo = input('');
  readonly closed = output<void>();

  protected readonly to = signal('');
  protected readonly note = signal('');
  protected readonly tier = signal('');
  protected readonly customerId = signal<number | null>(null);
  protected readonly customers = signal<CustomerLookup[]>([]);
  protected readonly preview = signal<EmailPreview | null>(null);
  protected readonly html = signal<SafeHtml>('');
  protected readonly configured = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  constructor() {
    effect(() => {
      if (!this.open()) return;
      untracked(() => { this.to.set(this.defaultTo()); this.note.set(''); this.error.set(''); this.preview.set(null); this.customerId.set(null); this.tier.set(''); void this.start(); });
    });
  }

  private async start() {
    try { this.configured.set((await this.api2.emailStatus()).configured); } catch { /* assume it works and let the send report */ }
    if (this.mode() === 'price-list') { try { this.customers.set((await this.api.customerLookup()).filter(c => c.customerType !== 'Walk-in')); } catch { /* optional */ } }
    await this.refresh();
  }

  protected pdfUrl() {
    return this.mode() === 'quotation' ? `/api/quotations/${this.quotationId()}/pdf` : `/api/price-list.pdf${this.tier() ? '?tier=' + this.tier() : ''}`;
  }

  protected async pickCustomer(id: number | null) {
    this.customerId.set(id);
    if (id !== null) {
      // The customer's email is only on the full record, which the server reads when it builds the preview.
      this.to.set('');
    }
    await this.refresh(true);
  }

  protected async refresh(fillTo = false) {
    this.error.set('');
    try {
      const to = this.to().trim() || null; const note = this.note().trim() || null;
      const p = this.mode() === 'quotation'
        ? await this.api2.previewQuotationEmail(this.quotationId()!, to, note)
        : await this.api2.previewPriceListEmail(this.customerId(), this.tier() || null, to, note);
      this.preview.set(p); this.html.set(this.san.bypassSecurityTrustHtml(p.html));   // sandboxed iframe: no scripts run
      if ((fillTo || !this.to().trim()) && p.to) this.to.set(p.to);
    } catch (e) { this.error.set(messageOf(e)); }
  }

  protected async send() {
    this.busy.set(true); this.error.set('');
    try {
      const to = this.to().trim(); const note = this.note().trim() || null;
      const r = this.mode() === 'quotation'
        ? await this.api2.sendQuotationEmail(this.quotationId()!, to, note)
        : await this.api2.sendPriceListEmail(this.customerId(), this.tier() || null, to, note);
      this.toasts.ok(`Sent to ${r.to}.`); this.closed.emit();
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
