import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api2 } from '../../core/api-more';
import { messageOf } from '../../core/api.service';
import { AssetKind } from '../../core/models-more';
import { Icon } from '../../shared/icon';
import { Toasts } from '../../shared/feedback';

const ASSETS: { kind: AssetKind; label: string; hint: string }[] = [
  { kind: 'logo', label: 'Company logo', hint: 'Printed at the top of every document.' },
  { kind: 'signature', label: 'Signature & stamp', hint: 'Printed above “Authorised signature” on receipts.' },
  { kind: 'waybill-stamp', label: 'Dispatch stamp', hint: 'Printed on waybills.' },
];

@Component({
  selector: 'app-company',
  imports: [ReactiveFormsModule, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Company & documents</h1></div>
      <p class="muted lede">These details are printed on receipts, quotations, waybills and price lists, and used in emails to customers.</p>

      <form [formGroup]="form" (ngSubmit)="save()" class="card card-pad" novalidate>
        <div class="form-grid">
          <div class="field span-2"><label for="c-n">Registered name</label><input id="c-n" class="input" formControlName="legalName" /></div>
          <div class="field span-2"><label for="c-a">Address</label><input id="c-a" class="input" formControlName="address" /></div>
          <div class="field"><label for="c-p">Phone</label><input id="c-p" class="input" inputmode="tel" formControlName="phone" /></div>
          <div class="field"><label for="c-e">Email</label><input id="c-e" class="input" type="email" formControlName="email" /></div>
          <div class="field"><label for="c-t">Tax ID (TIN)</label><input id="c-t" class="input" formControlName="taxId" /></div>
          <div class="field"><label for="c-v">Default VAT %</label><input id="c-v" class="input num" type="number" min="0" max="100" step="0.5" formControlName="defaultVatRate" /></div>
        </div>

        <h2 class="display sub">Bank accounts <span class="muted">(up to three, printed on invoices)</span></h2>
        <div formArrayName="banks" class="banks">
          @for (b of banks.controls; track $index; let i = $index) {
            <div class="bank" [formGroupName]="i">
              <div class="field"><label [for]="'bn' + i">Bank</label><input [id]="'bn' + i" class="input" formControlName="bankName" /></div>
              <div class="field"><label [for]="'ba' + i">Account name</label><input [id]="'ba' + i" class="input" formControlName="accountName" /></div>
              <div class="field"><label [for]="'bx' + i">Account number</label><input [id]="'bx' + i" class="input mono" inputmode="numeric" formControlName="accountNumber" /></div>
              <button type="button" class="btn btn-quiet btn-icon" (click)="removeBank(i)" [attr.aria-label]="'Remove bank ' + (i + 1)"><app-icon name="close" [size]="18" /></button>
            </div>
          }
        </div>
        @if (banks.length < 3) { <button type="button" class="btn btn-sm" (click)="addBank()"><app-icon name="plus" [size]="16" /> Add a bank</button> }

        <div class="save"><button type="submit" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ busy() ? 'Saving…' : 'Save company details' }}</button></div>
      </form>

      <section class="card card-pad images">
        <h2 class="display sub">Logo, signature and stamps</h2>
        <p class="muted">PNG or JPEG, up to 2 MB. Documents look fine without them.</p>
        <div class="grid">
          @for (a of assets; track a.kind) {
            <div class="asset">
              <strong>{{ a.label }}</strong><span class="muted sm">{{ a.hint }}</span>
              <div class="thumb">
                @if (has(a.kind)) { <img [src]="'/api/company/assets/' + a.kind + '?v=' + stamp()" [alt]="a.label" /> } @else { <span class="muted">Nothing uploaded</span> }
              </div>
              <div class="row">
                <label class="btn btn-sm"><input type="file" accept="image/png,image/jpeg" hidden (change)="upload(a.kind, $event)" /> {{ has(a.kind) ? 'Replace' : 'Upload' }}</label>
                @if (has(a.kind)) { <button type="button" class="btn btn-sm btn-danger" (click)="remove(a.kind)">Remove</button> }
              </div>
            </div>
          }
        </div>
      </section>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 46rem; } .sub { font-size: 1.4rem; margin: 1.5rem 0 .6rem; } .sm { font-size: .75rem; }
    .banks { display: grid; gap: .6rem; margin-bottom: .8rem; } .bank { display: grid; grid-template-columns: 1fr 1fr 1fr auto; gap: .6rem; align-items: end; }
    .save { margin-top: 1.25rem; } .images { margin-top: 1rem; } .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 1rem; margin-top: .8rem; }
    .asset { display: flex; flex-direction: column; gap: .4rem; } .thumb { height: 7rem; border: 1px dashed var(--line-strong); border-radius: var(--r-2); display: grid; place-items: center; background: #fff; overflow: hidden; }
    .thumb img { max-width: 100%; max-height: 100%; object-fit: contain; } .row { display: flex; gap: .5rem; }
    @media (max-width: 720px) { .bank { grid-template-columns: 1fr; } }
  `,
})
export class CompanyPage implements OnInit {
  private readonly api = inject(Api2);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  protected readonly assets = ASSETS;
  protected readonly busy = signal(false);
  protected readonly have = signal<string[]>([]);
  protected readonly stamp = signal(Date.now());
  protected readonly form = this.fb.nonNullable.group({
    legalName: ['', [Validators.required, Validators.maxLength(150)]], address: [''], phone: [''], email: ['', Validators.email], taxId: [''],
    defaultVatRate: [7.5, [Validators.min(0), Validators.max(100)]], defaultRebateRatePct: [1],
    banks: this.fb.array([] as ReturnType<CompanyPage['bankGroup']>[]),
  });
  protected get banks() { return this.form.controls.banks as FormArray; }

  private bankGroup(b = { bankName: '', accountName: '', accountNumber: '' }) {
    return this.fb.nonNullable.group({ bankName: [b.bankName, Validators.required], accountName: [b.accountName, Validators.required], accountNumber: [b.accountNumber, Validators.required] });
  }
  protected addBank() { this.banks.push(this.bankGroup()); }
  protected removeBank(i: number) { this.banks.removeAt(i); }
  protected has(k: AssetKind) { return this.have().includes(k); }

  async ngOnInit() {
    try {
      const p = await this.api.companyProfile();
      this.form.patchValue({ legalName: p.legalName, address: p.address, phone: p.phone, email: p.email, taxId: p.taxId, defaultVatRate: p.defaultVatRate, defaultRebateRatePct: p.defaultRebateRatePct });
      this.banks.clear(); p.banks.forEach(b => this.banks.push(this.bankGroup(b))); this.have.set(p.assets);
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected async save() {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    this.busy.set(true);
    try { await this.api.saveCompanyProfile({ ...v, defaultVatRate: Number(v.defaultVatRate), banks: v.banks as never }); this.toasts.ok('Company details saved.'); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async upload(kind: AssetKind, ev: Event) {
    const file = (ev.target as HTMLInputElement).files?.[0]; if (!file) return;
    try { await this.api.uploadAsset(kind, file); this.have.update(h => [...new Set([...h, kind])]); this.stamp.set(Date.now()); this.toasts.ok('Image saved.'); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async remove(kind: AssetKind) {
    try { await this.api.removeAsset(kind); this.have.update(h => h.filter(x => x !== kind)); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
