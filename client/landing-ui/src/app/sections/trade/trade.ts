import { Component, ElementRef, PLATFORM_ID, computed, inject, signal, viewChild } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';
import { Pixels } from '../../core/pixels';
import { PortalState } from '../partner/portal-state';

interface RegFile { id: number; kind: string; fileName: string; size: number; }
interface RegStore { key: string; name: string; }
interface RegState { step: number; email: string; emailVerified: boolean; data: Record<string, string>; files: RegFile[]; stores: RegStore[]; }

const DEVICE_KEY = 'trade-device';
const MAX_BYTES = 3_000_000;
const TYPES = ['application/pdf', 'image/jpeg', 'image/png', 'image/webp'];
export const STEPS = ['Email', 'Business', 'Contact', 'Registration', 'Documents', 'Review'];

/**
 * Trade registration, one step at a time: confirm your email, then business, contact, CAC and TIN, documents, and review. Every step is saved on the server,
 * so this device carries on where it stopped (a private token in this browser), and any device can carry on after confirming the email with a code.
 * An email that already belongs to a partner opens their account instead.
 */
@Component({
  selector: 'app-trade',
  imports: [RevealDirective],
  templateUrl: './trade.html',
  styleUrl: './trade.scss',
})
export class Trade {
  private readonly http = inject(HttpClient);
  private readonly pixels = inject(Pixels);
  private readonly portal = inject(PortalState);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('apply');
  protected readonly whatsappApply = whatsappHref('TRADE: I want to register my business for trade prices.');
  protected readonly steps = STEPS;

  /** 'wizard' while applying, 'done' after sending, 'account' when a partner is signed in. */
  protected readonly view = signal<'wizard' | 'done'>('wizard');
  /** The step on screen (1–6); never past the furthest step saved. */
  protected readonly step = signal(1);
  protected readonly state = signal<RegState | null>(null);
  protected readonly furthest = computed(() => this.state()?.step ?? 1);
  protected readonly email = signal('');
  protected readonly awaitingCode = signal<'' | 'code' | 'login'>('');
  protected readonly info = signal('');
  protected readonly error = signal('');
  protected readonly busy = signal(false);
  protected readonly resumed = signal('');
  protected readonly cooldown = signal(0);
  protected readonly doneMessage = signal('');

  // ------------------------------------------------------------------ opening

  protected async open(signIn = false): Promise<void> {
    this.error.set(''); this.info.set(''); this.resumed.set('');
    this.dialog()?.nativeElement.showModal();
    if (!this.browser) return;
    if (signIn) { this.close(); this.portal.open(); return; }
    if (read(DEVICE_KEY)) {
      try {
        const s = await firstValueFrom(this.http.get<RegState | null>('/api/trade/draft', { headers: this.headers() }));
        if (s && s.emailVerified) {
          this.state.set(s); this.view.set('wizard'); this.step.set(Math.min(s.step, STEPS.length));
          this.resumed.set(`Welcome back. Your answers are saved: you're on step ${this.step()} of ${STEPS.length}.`);
          return;
        }
      } catch { /* start fresh */ }
    }
    this.view.set(this.view() === 'done' ? 'wizard' : this.view());
    this.step.set(1);
  }

  protected close(): void { this.dialog()?.nativeElement.close(); }
  /** Closes on a click on the dark backdrop. Returns nothing: a handler returning false would cancel the click, and with it the form's submit. */
  protected backdrop(e: MouseEvent): void { if (e.target === e.currentTarget) this.close(); }

  // ------------------------------------------------------------------ step 1: email and code

  protected async start(form: HTMLFormElement): Promise<void> {
    if (!form.reportValidity()) return;
    const email = String(new FormData(form).get('email') ?? '').trim();
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ next: string; email: string; deviceToken: string | null; state: RegState | null; message: string }>(
        '/api/trade/start', { email }, { headers: this.headers() }));
      this.email.set(email);
      // an existing partner: the portal takes over (signed straight in on a trusted browser, otherwise at the code step; no second code is sent)
      if (r.next === 'login' || r.next === 'signedin') { this.close(); this.portal.continueSignIn(email, r.next, (r as { session?: string }).session); return; }
      if (r.deviceToken) write(DEVICE_KEY, r.deviceToken);
      if (r.next === 'resume' && r.state) { this.state.set(r.state); this.step.set(Math.min(r.state.step, STEPS.length)); this.resumed.set(r.message); return; }
      this.awaitingCode.set(r.next === 'login' ? 'login' : 'code');
      this.info.set(r.message);
      this.startCooldown();
    });
  }

  protected async verify(form: HTMLFormElement): Promise<void> {
    if (!form.reportValidity()) return;
    const code = String(new FormData(form).get('code') ?? '').replace(/\D/g, '');
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ partner: boolean; session?: string; deviceToken?: string | null; state?: RegState }>(
        '/api/trade/verify', { email: this.email(), code }, { headers: this.headers() }));
      this.awaitingCode.set(''); this.info.set('');
      if (r.partner && r.session) { this.close(); this.portal.continueSignIn(this.email(), 'signedin', r.session); return; }
      if (r.deviceToken) write(DEVICE_KEY, r.deviceToken);
      this.state.set(r.state!); this.step.set(Math.min(r.state!.step, STEPS.length));
      if (r.state!.step > 2) this.resumed.set(`Email confirmed. Your answers are saved: carrying on at step ${this.step()}.`);
    });
  }

  protected async resend(): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(this.http.post('/api/trade/resend', { email: this.email() }, { headers: this.headers() }));
      this.info.set('A new code is on its way. Check your inbox (and spam folder).');
      this.startCooldown();
    });
  }

  protected changeEmail(): void { this.awaitingCode.set(''); this.info.set(''); this.error.set(''); }

  private startCooldown(): void {
    this.cooldown.set(60);
    const t = setInterval(() => { this.cooldown.update(n => n - 1); if (this.cooldown() <= 0) clearInterval(t); }, 1000);
  }

  // ------------------------------------------------------------------ steps 2–4: saved one at a time

  protected value(key: string): string { return this.state()?.data[key] ?? ''; }

  protected async save(stepNo: number, form: HTMLFormElement): Promise<void> {
    if (!form.reportValidity()) return;
    const data = Object.fromEntries(new FormData(form).entries());
    await this.run(async () => {
      const s = await firstValueFrom(this.http.put<RegState>(`/api/trade/draft/step/${stepNo}`, data, { headers: this.headers() }));
      this.state.set(s); this.resumed.set(''); this.step.set(stepNo + 1);
    });
  }

  /** Go back to look at or change an earlier step (only steps already reached). */
  protected goTo(n: number): void { if (n >= 2 && n <= this.furthest() && this.view() === 'wizard' && !this.awaitingCode()) { this.error.set(''); this.step.set(n); } }

  // ------------------------------------------------------------------ step 5: documents

  protected file(kind: string): RegFile | undefined { return this.state()?.files.find(f => f.kind === kind); }

  protected async upload(kind: string, input: HTMLInputElement): Promise<void> {
    this.error.set('');
    const f = input.files?.[0]; input.value = '';
    if (!f) return;
    if (!TYPES.includes(f.type)) { this.error.set('Attach a PDF, JPG, PNG or WebP file.'); return; }
    const file = f.type.startsWith('image/') ? await shrink(f) : f;
    if (file.size > MAX_BYTES) { this.error.set('That file is over 3 MB. A phone photo or a scanned PDF is fine.'); return; }
    const body = new FormData(); body.append('kind', kind); body.append('file', file, file.name);
    await this.run(async () => this.state.set(await firstValueFrom(this.http.post<RegState>('/api/trade/draft/files', body, { headers: this.headers() }))));
  }

  protected async removeFile(f: RegFile): Promise<void> {
    await this.run(async () => this.state.set(await firstValueFrom(this.http.delete<RegState>(`/api/trade/draft/files/${f.id}`, { headers: this.headers() }))));
  }

  protected async documentsDone(): Promise<void> {
    await this.run(async () => {
      this.state.set(await firstValueFrom(this.http.post<RegState>('/api/trade/draft/documents-done', {}, { headers: this.headers() })));
      this.step.set(6);
    });
  }

  // ------------------------------------------------------------------ step 6: review and send

  protected storeLabel(): string {
    const v = this.value('stores'); const s = this.state()?.stores ?? [];
    return v === 'both' ? s.map(x => x.name).join(' and ') : s.find(x => x.key === v)?.name ?? v;
  }

  protected async submit(): Promise<void> {
    await this.run(async () => {
      const r = await firstValueFrom(this.http.post<{ message: string; stores: string[] }>('/api/trade/draft/submit', {}, { headers: this.headers() }));
      this.doneMessage.set(r.message); this.view.set('done');
      remove(DEVICE_KEY); this.state.set(null); this.step.set(1);
      this.pixels.lead();
    });
  }

  protected kb(n: number): string { return n < 1024 * 1024 ? `${Math.max(1, Math.round(n / 1024))} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`; }

  // ------------------------------------------------------------------ plumbing

  private headers(): HttpHeaders {
    let h = new HttpHeaders();
    const d = read(DEVICE_KEY); if (d) h = h.set('X-Device-Token', d);
    const tb = read('trusted-browser'); if (tb) h = h.set('X-Trusted-Browser', tb);
    return h;
  }

  private async run(work: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.error.set('');
    try { await work(); }
    catch (e) {
      const err = e as HttpErrorResponse;
      this.error.set(err.status === 429 ? 'Too many attempts from this connection. Please wait a few minutes and try again.'
        : err.error?.title ?? err.error?.detail ?? 'Something went wrong. Please try again, or apply on WhatsApp.');
    } finally { this.busy.set(false); }
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

/** The device token lives in localStorage (carry on tomorrow); the partner session only for this tab (signed out when it closes). */
function read(k: string, session = false): string | null { try { return (session ? sessionStorage : localStorage).getItem(k); } catch { return null; } }
function write(k: string, v: string, session = false): void { try { (session ? sessionStorage : localStorage).setItem(k, v); } catch { /* private mode */ } }
function remove(k: string, session = false): void { try { (session ? sessionStorage : localStorage).removeItem(k); } catch { /* private mode */ } }
