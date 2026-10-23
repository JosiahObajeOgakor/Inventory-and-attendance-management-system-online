import { Component, ElementRef, PLATFORM_ID, inject, signal, viewChild } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

type StoreChoice = 'chewypets' | 'candid' | 'both';
const MAX_BYTES = 3_000_000;
const TYPES = ['application/pdf', 'image/jpeg', 'image/png', 'image/webp'];

/**
 * Pet shops, wholesalers and distributors apply for trade prices: business details, CAC number, TIN and the CAC certificate.
 * A manager checks and approves; until then everyone sees retail prices. Photos are shrunk in the browser so a phone snapshot uploads in seconds.
 */
@Component({
  selector: 'app-trade',
  imports: [RevealDirective],
  templateUrl: './trade.html',
  styleUrl: './trade.scss',
})
export class Trade {
  private readonly http = inject(HttpClient);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('apply');
  protected readonly whatsappApply = whatsappHref('TRADE: I want to register my business for trade prices.');
  protected readonly tier = signal<'Wholesaler' | 'Distributor'>('Wholesaler');
  protected readonly store = signal<StoreChoice>('both');
  protected readonly cac = signal<File | null>(null);
  protected readonly cacInfo = signal('');
  protected readonly sending = signal(false);
  protected readonly done = signal('');
  protected readonly error = signal('');

  protected open(): void {
    this.done.set(''); this.error.set('');
    this.dialog()?.nativeElement.showModal();
  }
  protected close(): void { this.dialog()?.nativeElement.close(); }
  /** Closes on a click on the dark backdrop. Returns nothing: a handler returning false would cancel the click, and with it the form's submit. */
  protected backdrop(e: MouseEvent): void { if (e.target === e.currentTarget) this.close(); }

  protected async pick(input: HTMLInputElement): Promise<void> {
    this.error.set('');
    const f = input.files?.[0] ?? null;
    if (!f) { this.cac.set(null); this.cacInfo.set(''); return; }
    if (!TYPES.includes(f.type)) { this.fail(input, 'Attach a PDF, JPG, PNG or WebP file.'); return; }
    const file = f.type.startsWith('image/') ? await shrink(f) : f;
    if (file.size > MAX_BYTES) { this.fail(input, 'That file is over 3 MB. A phone photo or a scanned PDF is fine.'); return; }
    this.cac.set(file);
    this.cacInfo.set(`${f.name} · ${Math.max(1, Math.round(file.size / 1024))} KB`);
  }

  private fail(input: HTMLInputElement, message: string): void { input.value = ''; this.cac.set(null); this.cacInfo.set(''); this.error.set(message); }

  protected async submit(form: HTMLFormElement): Promise<void> {
    if (this.sending() || !this.browser) return;
    if (!form.reportValidity()) return;
    if (!this.cac()) { this.error.set('Please attach your CAC certificate.'); return; }
    this.sending.set(true); this.error.set('');
    const data = new FormData(form);
    const body = new FormData();
    for (const k of ['businessName', 'contactName', 'phone', 'email', 'address', 'cacNumber', 'taxId', 'description', 'website']) body.append(k, String(data.get(k) ?? ''));
    body.append('tier', this.tier());
    body.append('cac', this.cac()!, this.cac()!.name);
    const stores: string[] = this.store() === 'both' ? ['chewypets', 'candid'] : [this.store()];
    try {
      let message = '';
      for (const s of stores) {
        const r = await firstValueFrom(this.http.post<{ message: string }>(`/api/webchat/trade/apply?store=${s}`, body));
        message = r.message;
      }
      this.done.set(message);
      form.reset(); this.cac.set(null); this.cacInfo.set('');
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.error.set(err.status === 429 ? 'Too many attempts from this connection. Please wait 15 minutes and try again.'
        : err.error?.detail ?? err.error?.title ?? 'We couldn\'t send your application just now. Please try again, or apply on WhatsApp.');
    } finally { this.sending.set(false); }
  }
}

/** Phone photos are often 4–8 MB: redraw at most 1600px on the long side as JPEG, which keeps a certificate readable at a few hundred KB. */
async function shrink(file: File): Promise<File> {
  if (file.size < 400_000) return file;
  try {
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, 1600 / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(bitmap.width * scale); canvas.height = Math.round(bitmap.height * scale);
    canvas.getContext('2d')!.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob | null>(res => canvas.toBlob(res, 'image/jpeg', 0.82));
    return blob && blob.size < file.size ? new File([blob], file.name.replace(/\.\w+$/, '') + '.jpg', { type: 'image/jpeg' }) : file;
  } catch { return file; }
}
