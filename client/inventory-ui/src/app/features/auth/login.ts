import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { CompanyChoice } from '../../core/models';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="split">
      <section class="panel" aria-hidden="true">
        <span class="eyebrow">Warehouse · sales · stock</span>
        <h1 class="display">Count it.<br />Sell it.<br />Know it.</h1>
        <p>One shared record for the counter and the warehouse floor, wherever you sign in from.</p>
        <div class="slats"></div>
      </section>

      <section class="formside">
        <form [formGroup]="form" (ngSubmit)="submit()" class="card" novalidate>
          <h2 class="display">Sign in</h2>

          <fieldset class="field biz">
            <legend class="label">Which business are you opening?</legend>
            <div class="choices">
              @for (c of companies(); track c.key) {
                <label class="choice" [attr.data-co]="c.key" [class.on]="form.controls.company.value === c.key">
                  <input type="radio" formControlName="company" [value]="c.key" (change)="pick(c.key)" />
                  <span class="dot"></span>
                  <span>{{ c.displayName }}</span>
                </label>
              }
            </div>
          </fieldset>

          <div class="field">
            <label for="u">Username</label>
            <input id="u" class="input" formControlName="username" autocomplete="username" autocapitalize="none" spellcheck="false" />
          </div>
          <div class="field">
            <label for="p">Password</label>
            <input id="p" class="input" type="password" formControlName="password" autocomplete="current-password" />
          </div>

          @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

          <button class="btn btn-primary" type="submit" [disabled]="busy() || form.invalid">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
          <p class="muted small">Forgot your password? Ask an administrator to reset it.</p>
        </form>
      </section>
    </div>`,
  styles: `
    .split { display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(0, 1fr); min-height: 100dvh; }
    .panel { background: var(--ink); color: #fff; padding: clamp(2rem, 6vw, 5rem); display: flex; flex-direction: column; justify-content: center; gap: 1.5rem; position: relative; overflow: hidden; }
    .panel .eyebrow { color: var(--signal); }
    .panel h1 { font-size: clamp(3.4rem, 8vw, 6.8rem); line-height: .88; }
    .panel p { max-width: 28rem; color: #b7c3bc; font-size: 1.05rem; }
    .slats { position: absolute; right: -2rem; bottom: 2.5rem; width: 15rem; height: 9rem; opacity: .9;
      background: repeating-linear-gradient(90deg, var(--brand) 0 2.6rem, transparent 2.6rem 3rem); border-radius: 2px; transition: background .25s; }
    .slats::after { content: ''; position: absolute; left: 0; right: 0; bottom: -1.1rem; height: .6rem; background: var(--signal); }
    .formside { display: grid; place-items: center; padding: 2rem 1rem; }
    form { width: min(26rem, 100%); padding: 2rem 1.75rem; display: flex; flex-direction: column; gap: 1.05rem; }
    h2 { font-size: 2.4rem; }
    fieldset { border: 0; padding: 0; margin: 0; }
    .choices { display: grid; gap: .5rem; margin-top: .4rem; }
    .choice { display: flex; align-items: center; gap: .7rem; padding: .7rem .85rem; border: 1.5px solid var(--line-strong); border-radius: var(--r-2); cursor: pointer; font-weight: 600; background: #fff; transition: border-color .12s, background .12s; }
    .choice input { position: absolute; opacity: 0; }
    .choice .dot { width: 1rem; height: 1rem; border-radius: 50%; border: 2px solid var(--line-strong); flex: none; }
    .choice[data-co='chewypets'] { --c: #1f6b4f; --t: #dcebe3; } .choice[data-co='candid'] { --c: #6a2c5b; --t: #ecdfe8; }
    .choice.on { border-color: var(--c); background: var(--t); } .choice.on .dot { border-color: var(--c); background: var(--c); box-shadow: inset 0 0 0 3px var(--t); }
    .choice:has(input:focus-visible) { outline: 3px solid var(--signal); outline-offset: 2px; }
    .small { font-size: .8125rem; }
    @media (max-width: 860px) { .split { grid-template-columns: minmax(0, 1fr); } .panel { min-height: 15rem; padding: 1.75rem 1.25rem; gap: .8rem; } .panel h1 { font-size: 3rem; } .panel p, .slats { display: none; } }
  `,
})
export class Login implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  protected readonly companies = signal<CompanyChoice[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly form = this.fb.nonNullable.group({
    company: [this.auth.selected(), Validators.required],
    username: ['', Validators.required],
    password: ['', Validators.required],
  });

  async ngOnInit() {
    if (this.auth.me()) { void this.router.navigateByUrl('/dashboard'); return; }
    try {
      const list = await this.api.companies();
      this.companies.set(list);
      if (!list.some(c => c.key === this.form.controls.company.value) && list[0]) this.pick(list[0].key);
    } catch { this.error.set(messageOf(null)); }
  }

  protected pick(key: string) { this.form.controls.company.setValue(key); this.auth.selected.set(key); }

  protected async submit() {
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true); this.error.set('');
    const v = this.form.getRawValue();
    try {
      const me = await this.auth.login(v.username, v.password, v.company);
      await this.router.navigateByUrl(me.mustChangePassword ? '/change-password' : '/dashboard');
    } catch (e) {
      this.error.set(messageOf(e));
      this.form.controls.password.reset('');
    } finally { this.busy.set(false); }
  }
}
