import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Api, messageOf } from '../../core/api.service';
import { Auth } from '../../core/auth.service';
import { UserRow } from '../../core/models';
import { Icon } from '../../shared/icon';
import { Modal } from '../../shared/modal';
import { Toasts } from '../../shared/feedback';
import { StampTimePipe } from '../../shared/ui';

@Component({
  selector: 'app-users',
  imports: [ReactiveFormsModule, Icon, Modal, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <h1>People & access</h1>
        <div class="actions"><button type="button" class="btn btn-primary" (click)="openNew()"><app-icon name="plus" [size]="18" /> Add person</button></div>
      </div>
      @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
      <section class="card">
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Person</th><th>Role</th><th>Can open</th><th>Last sign-in</th><th>Status</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (u of users(); track u.id) {
              <tr><td><span class="strong">{{ u.fullName }}</span><div class="muted mono sm">{{ u.username }}</div></td>
                <td>{{ u.role === 'ADMIN' ? 'Administrator' : 'Warehouse clerk' }}</td><td>{{ names(u.companyAccess) }}</td><td>{{ u.lastLoginAt ? (u.lastLoginAt | stampTime) : 'Never' }}</td>
                <td>@if (!u.isActive) { <span class="stamp stamp-bad">Disabled</span> } @else if (u.mustChangePassword) { <span class="stamp stamp-warn">Must set password</span> } @else { <span class="stamp stamp-ok">Active</span> }</td>
                <td class="actions"><button type="button" class="btn btn-sm" (click)="openEdit(u)">Edit</button> <button type="button" class="btn btn-sm" (click)="openReset(u)">Reset password</button></td></tr>
            }
          </tbody>
        </table></div>
      </section>
    </div>

    <app-modal [open]="formOpen()" [heading]="editing() ? 'Edit person' : 'Add person'" (closed)="formOpen.set(false)">
      <form id="uf" [formGroup]="form" (ngSubmit)="save()" class="form-grid" novalidate>
        <div class="field span-2"><label for="u-n">Full name</label><input id="u-n" class="input" formControlName="fullName" /></div>
        @if (!editing()) {
          <div class="field"><label for="u-u">Username</label><input id="u-u" class="input" formControlName="username" autocapitalize="none" autocomplete="off" /></div>
          <div class="field"><label for="u-p">First password</label><input id="u-p" class="input" type="text" formControlName="password" autocomplete="off" /><span class="hint">They must change it when they first sign in.</span></div>
        }
        <div class="field"><label for="u-r">Role</label><select id="u-r" class="input" formControlName="role"><option value="CLERK">Warehouse clerk</option><option value="ADMIN">Administrator</option></select></div>
        <fieldset class="field"><legend class="label">Can open</legend>
          @for (c of companyOptions(); track c.key) { <label class="check"><input type="checkbox" [checked]="hasCompany(c.key)" (change)="toggleCompany(c.key, $any($event.target).checked)" /> {{ c.displayName }}</label> }</fieldset>
        @if (editing()) { <label class="check span-2"><input type="checkbox" formControlName="isActive" /> This person can sign in</label> }
      </form>
      <ng-container modal-actions><button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button><button type="submit" form="uf" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ editing() ? 'Save changes' : 'Add person' }}</button></ng-container>
    </app-modal>

    <app-modal [open]="!!resetting()" heading="Reset password" (closed)="resetting.set(null)">
      @if (resetting(); as u) {
        <p>Set a temporary password for <strong>{{ u.fullName }}</strong>. They’ll be asked to choose their own at the next sign-in, and any lock is cleared.</p>
        <form id="rf" [formGroup]="resetForm" (ngSubmit)="reset()" class="field" style="margin-top:.9rem" novalidate>
          <label for="r-p">Temporary password</label><input id="r-p" class="input" type="text" formControlName="password" autocomplete="off" /><span class="hint">At least 8 characters, with letters and numbers.</span>
        </form>
      }
      <ng-container modal-actions><button type="button" class="btn" (click)="resetting.set(null)">Cancel</button><button type="submit" form="rf" class="btn btn-primary" [disabled]="resetForm.invalid || busy()">Set password</button></ng-container>
    </app-modal>`,
  styles: `.sm { font-size: .75rem; } fieldset { border: 0; padding: 0; margin: 0; display: flex; gap: .5rem; }`,
})
export class UsersPage implements OnInit {
  private readonly api = inject(Api);
  private readonly fb = inject(FormBuilder);
  private readonly toasts = inject(Toasts);
  protected readonly auth = inject(Auth);
  protected readonly users = signal<UserRow[]>([]);
  protected readonly error = signal('');
  protected readonly formOpen = signal(false);
  protected readonly editing = signal<UserRow | null>(null);
  protected readonly resetting = signal<UserRow | null>(null);
  protected readonly busy = signal(false);
  private access = new Set<string>();

  protected readonly form = this.fb.nonNullable.group({
    fullName: ['', Validators.required], username: [''], password: [''], role: ['CLERK'], isActive: [true],
  });
  protected readonly resetForm = this.fb.nonNullable.group({ password: ['', [Validators.required, Validators.minLength(8)]] });

  protected companyOptions() { return this.auth.me()?.companies ?? []; }
  protected names(access: string) { return access.split(',').map(k => this.companyOptions().find(c => c.key === k.trim())?.displayName ?? k).join(' · '); }
  protected hasCompany(k: string) { return this.access.has(k); }
  protected toggleCompany(k: string, on: boolean) { if (on) this.access.add(k); else this.access.delete(k); }

  ngOnInit() { void this.load(); }
  private async load() { try { this.users.set(await this.api.users()); } catch (e) { this.error.set(messageOf(e)); } }

  protected openNew() {
    this.editing.set(null); this.access = new Set(this.companyOptions().map(c => c.key));
    this.form.reset({ fullName: '', username: '', password: '', role: 'CLERK', isActive: true });
    this.form.controls.username.setValidators(Validators.required); this.form.controls.password.setValidators([Validators.required, Validators.minLength(8)]);
    this.form.controls.username.updateValueAndValidity(); this.form.controls.password.updateValueAndValidity();
    this.formOpen.set(true);
  }
  protected openEdit(u: UserRow) {
    this.editing.set(u); this.access = new Set(u.companyAccess.split(',').map(s => s.trim()).filter(Boolean));
    this.form.reset({ fullName: u.fullName, username: u.username, password: '', role: u.role, isActive: u.isActive });
    this.form.controls.username.clearValidators(); this.form.controls.password.clearValidators();
    this.form.controls.username.updateValueAndValidity(); this.form.controls.password.updateValueAndValidity();
    this.formOpen.set(true);
  }
  protected openReset(u: UserRow) { this.resetting.set(u); this.resetForm.reset({ password: '' }); }

  protected async save() {
    if (this.form.invalid) return;
    if (!this.access.size) { this.toasts.error('Choose at least one business this person can open.'); return; }
    const v = this.form.getRawValue(); const companies = [...this.access];
    this.busy.set(true);
    try {
      const e = this.editing();
      if (e) await this.api.updateUser(e.id, { fullName: v.fullName.trim(), role: v.role, companies, isActive: v.isActive });
      else await this.api.createUser({ fullName: v.fullName.trim(), username: v.username.trim(), password: v.password, role: v.role, companies });
      this.toasts.ok(e ? 'Changes saved.' : 'Person added. They’ll set their own password when they first sign in.');
      this.formOpen.set(false); await this.load();
    } catch (err) { this.toasts.error(messageOf(err)); } finally { this.busy.set(false); }
  }

  protected async reset() {
    const u = this.resetting(); if (!u || this.resetForm.invalid) return;
    this.busy.set(true);
    try { await this.api.resetPassword(u.id, this.resetForm.controls.password.value); this.toasts.ok(`Password set for ${u.fullName}.`); this.resetting.set(null); await this.load(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }
}
