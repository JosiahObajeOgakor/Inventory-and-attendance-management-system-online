import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { Toasts } from '../../shared/feedback';

const same = (g: AbstractControl): ValidationErrors | null =>
  g.get('next')?.value === g.get('again')?.value ? null : { mismatch: true };

@Component({
  selector: 'app-change-password',
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap">
      <form class="card" [formGroup]="form" (ngSubmit)="save()" novalidate>
        <h1 class="display">{{ auth.me()?.mustChangePassword ? 'Choose a new password' : 'Change my password' }}</h1>
        @if (auth.me()?.mustChangePassword) { <p class="notice info">An administrator set your current password. Choose your own before you continue.</p> }
        <div class="field"><label for="c">Current password</label><input id="c" class="input" type="password" formControlName="current" autocomplete="current-password" /></div>
        <div class="field"><label for="n">New password</label>
          <input id="n" class="input" type="password" formControlName="next" autocomplete="new-password" />
          <span class="hint">At least 8 characters, with letters and numbers.</span></div>
        <div class="field"><label for="a">Type the new password again</label><input id="a" class="input" type="password" formControlName="again" autocomplete="new-password" />
          @if (form.hasError('mismatch') && form.controls.again.touched) { <span class="error">The two passwords don’t match.</span> }</div>
        @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
        <div class="row">
          @if (!auth.me()?.mustChangePassword) { <button type="button" class="btn" (click)="back()">Cancel</button> }
          <button class="btn btn-primary" type="submit" [disabled]="busy() || form.invalid">Save new password</button>
        </div>
      </form>
    </div>`,
  styles: `.wrap { display: grid; place-items: center; min-height: 100dvh; padding: 1.5rem; } form { width: min(26rem, 100%); padding: 1.75rem; display: flex; flex-direction: column; gap: 1rem; } h1 { font-size: 2rem; } .row { display: flex; justify-content: flex-end; gap: .5rem; }`,
})
export class ChangePassword {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  private readonly fb = inject(FormBuilder);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly form = this.fb.nonNullable.group(
    { current: ['', Validators.required], next: ['', [Validators.required, Validators.minLength(8)]], again: ['', Validators.required] },
    { validators: same });

  protected back() { void this.router.navigateByUrl('/dashboard'); }

  protected async save() {
    if (this.form.invalid) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.changePassword(this.form.controls.current.value, this.form.controls.next.value);
      this.auth.me.set(await this.api.me());
      this.toasts.ok('Password changed.');
      await this.router.navigateByUrl('/dashboard');
    } catch (e) { this.error.set(messageOf(e)); } finally { this.busy.set(false); }
  }
}
