import { ChangeDetectionStrategy, Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../../core/api.service';
import { roleLabel } from '../../core/auth.service';
import { PERMISSION_GROUPS, PermissionGroup } from '../../core/permission-catalog';
import { Modal } from '../../shared/modal';
import { Confirm, Toasts } from '../../shared/feedback';

export interface RoleRow { name: string; builtIn: boolean; users: number; permissions: string[]; }

/**
 * What each role may see and do, ticked on and off by the CEO. The API checks the same permissions on every call; a change reaches everyone
 * in the role within a minute, without signing anyone out.
 */
@Component({
  selector: 'app-roles',
  imports: [FormsModule, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="roles">
      <aside class="card list" aria-label="Roles">
        <div class="list-head"><strong>Roles</strong><button type="button" class="btn btn-sm" (click)="newOpen.set(true)">New role</button></div>
        <ul>
          @for (r of roles(); track r.name) {
            <li><button type="button" [class.on]="r.name === selected()" (click)="pick(r.name)">
              <span class="nm">{{ label(r.name) }}</span>
              <span class="meta">{{ r.users }} {{ r.users === 1 ? 'person' : 'people' }} · {{ r.name === 'ADMIN' ? 'everything' : r.permissions.length + ' on' }}</span>
            </button></li>
          }
        </ul>
      </aside>

      <section class="card editor">
        @if (current(); as r) {
          <header class="ed-head">
            <div>
              <h2>{{ label(r.name) }}</h2>
              <p class="muted">
                @if (locked()) { The CEO role always has every permission, so the business can never lock itself out. }
                @else { {{ granted().size }} of {{ total }} switched on. Changes reach everyone in this role within a minute. }
              </p>
            </div>
            @if (!locked()) {
              <div class="ed-actions">
                @if (!r.builtIn) { <button type="button" class="btn btn-ghost danger" [disabled]="busy()" (click)="remove(r)">Delete role</button> }
                <button type="button" class="btn" [disabled]="!dirty() || busy()" (click)="pick(r.name)">Undo</button>
                <button type="button" class="btn btn-primary" [disabled]="!dirty() || busy()" (click)="save()">{{ busy() ? 'Saving…' : 'Save' }}</button>
              </div>
            }
          </header>

          <div class="groups">
            @for (g of groups; track g.title) {
              <fieldset class="grp" [disabled]="locked()">
                <legend>
                  <span>{{ g.title }}</span>
                  @if (!locked()) { <label class="all"><input type="checkbox" [checked]="allOn(g)" [indeterminate]="someOn(g)" (change)="setGroup(g, $any($event.target).checked)" /> All</label> }
                </legend>
                @for (p of g.items; track p.code) {
                  <label class="perm">
                    <span class="txt">{{ p.label }} <span class="kind" [class.act]="p.kind === 'act'">{{ p.kind === 'see' ? 'See' : 'Do' }}</span></span>
                    <input type="checkbox" role="switch" class="switch" [checked]="locked() || granted().has(p.code)" (change)="set(p.code, $any($event.target).checked)" />
                  </label>
                }
              </fieldset>
            }
          </div>
        } @else { <p class="muted">Choose a role.</p> }
      </section>
    </div>

    <app-modal [open]="newOpen()" heading="New role" (closed)="newOpen.set(false)">
      <form id="nr" class="form-grid" (submit)="$event.preventDefault(); create()">
        <div class="field span-2"><label for="nr-n">Role name</label><input id="nr-n" name="n" class="input" [(ngModel)]="newName" maxlength="60" placeholder="e.g. Shop attendant" /></div>
        <div class="field span-2"><label for="nr-c">Start from</label>
          <select id="nr-c" name="c" class="input" [(ngModel)]="copyFrom">
            <option [ngValue]="null">Nothing (switch on what they need)</option>
            @for (r of roles(); track r.name) { <option [ngValue]="r.name">{{ label(r.name) }}</option> }
          </select></div>
      </form>
      <ng-container modal-actions><button type="button" class="btn" (click)="newOpen.set(false)">Cancel</button><button type="submit" form="nr" class="btn btn-primary" [disabled]="!newName.trim() || busy()">Create role</button></ng-container>
    </app-modal>`,
  styles: `
    .roles { display: grid; grid-template-columns: 16rem minmax(0, 1fr); gap: 1rem; align-items: start; }
    .list { padding: .5rem; position: sticky; top: 4.5rem; max-height: calc(100dvh - 6rem); overflow: auto; }
    .list-head { display: flex; justify-content: space-between; align-items: center; padding: .4rem .5rem .6rem; }
    .list ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
    .list li button { width: 100%; display: flex; flex-direction: column; align-items: flex-start; gap: .05rem; padding: .55rem .6rem; border: 0; border-radius: 8px; background: none; text-align: left; cursor: pointer; color: inherit; min-height: 44px; }
    .list li button:hover { background: var(--surface-2, #f1f2f5); }
    .list li button.on { background: color-mix(in srgb, var(--brand) 10%, transparent); box-shadow: inset 3px 0 0 var(--brand); }
    .nm { font-weight: 600; font-size: .9rem; } .meta { font-size: .75rem; color: var(--muted, #5b6170); }
    .editor { padding: 1.1rem 1.2rem; }
    .ed-head { display: flex; justify-content: space-between; gap: 1rem; flex-wrap: wrap; align-items: flex-start; margin-bottom: 1rem; }
    .ed-head h2 { margin: 0 0 .2rem; font-size: 1.2rem; } .ed-actions { display: flex; gap: .5rem; flex-wrap: wrap; } .danger { color: var(--danger, #b42318); }
    .groups { display: grid; grid-template-columns: repeat(auto-fill, minmax(19rem, 1fr)); gap: .9rem; }
    .grp { border: 1px solid var(--line, #e3e5ea); border-radius: 12px; padding: .3rem .9rem .6rem; margin: 0; min-width: 0; }
    .grp legend { display: flex; width: 100%; justify-content: space-between; align-items: center; gap: .5rem; padding: 0 .3rem; font-weight: 700; font-size: .85rem; }
    .all { display: inline-flex; align-items: center; gap: .3rem; font-weight: 500; font-size: .78rem; color: var(--muted, #5b6170); cursor: pointer; }
    .perm { display: flex; justify-content: space-between; align-items: center; gap: .75rem; padding: .45rem 0; border-top: 1px solid var(--line, #e3e5ea); cursor: pointer; font-size: .875rem; min-height: 44px; }
    .grp legend + .perm { border-top: 0; }
    .kind { font-size: .65rem; font-weight: 700; letter-spacing: .05em; text-transform: uppercase; padding: .05rem .35rem; border-radius: 99px; background: var(--surface-2, #f1f2f5); color: var(--muted, #5b6170); margin-left: .25rem; }
    .kind.act { background: #fff4e0; color: #8a4b00; }
    .switch { appearance: none; flex: none; width: 2.3rem; height: 1.3rem; border-radius: 99px; background: #c9ccd3; position: relative; cursor: pointer; transition: background .15s; margin: 0; }
    .switch::after { content: ''; position: absolute; top: .15rem; left: .15rem; width: 1rem; height: 1rem; border-radius: 50%; background: #fff; transition: transform .15s; box-shadow: 0 1px 2px rgb(0 0 0 / .25); }
    .switch:checked { background: var(--brand); } .switch:checked::after { transform: translateX(1rem); }
    .switch:focus-visible { outline: 2px solid var(--brand); outline-offset: 2px; }
    fieldset:disabled .switch { opacity: .55; cursor: default; }
    @media (max-width: 860px) { .roles { grid-template-columns: minmax(0, 1fr); } .list { position: static; max-height: none; } }
    @media (prefers-reduced-motion: reduce) { .switch, .switch::after { transition: none; } }
  `,
})
export class RolesEditor implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  /** Tells the People tab that the list of roles changed (a new role can be given to someone). */
  readonly changed = output<void>();

  protected readonly groups: PermissionGroup[] = PERMISSION_GROUPS;
  protected readonly total = PERMISSION_GROUPS.reduce((n, g) => n + g.items.length, 0);
  protected readonly roles = signal<RoleRow[]>([]);
  protected readonly selected = signal<string>('CLERK');
  protected readonly granted = signal<Set<string>>(new Set());
  protected readonly busy = signal(false);
  protected readonly newOpen = signal(false);
  protected newName = ''; protected copyFrom: string | null = 'CLERK';

  protected readonly label = roleLabel;
  protected readonly current = computed(() => this.roles().find(r => r.name === this.selected()) ?? null);
  protected readonly locked = computed(() => this.selected() === 'ADMIN');
  protected readonly dirty = computed(() => {
    const r = this.current(); if (!r) return false;
    const a = new Set(r.permissions), b = this.granted();
    return a.size !== b.size || [...a].some(p => !b.has(p));
  });

  ngOnInit() { void this.load(); }

  private async load(keep = this.selected()) {
    try {
      const rows = await firstValueFrom(this.http.get<RoleRow[]>('/api/roles'));
      rows.sort((a, b) => rank(a.name) - rank(b.name) || a.name.localeCompare(b.name));
      this.roles.set(rows);
      this.pick(rows.some(r => r.name === keep) ? keep : rows[0]?.name ?? 'CLERK');
    } catch (e) { this.toasts.error(messageOf(e)); }
  }

  protected pick(name: string) { this.selected.set(name); this.granted.set(new Set(this.roles().find(r => r.name === name)?.permissions ?? [])); }
  protected set(code: string, on: boolean) { const next = new Set(this.granted()); if (on) next.add(code); else next.delete(code); this.granted.set(next); }
  protected setGroup(g: PermissionGroup, on: boolean) { const next = new Set(this.granted()); for (const p of g.items) { if (on) next.add(p.code); else next.delete(p.code); } this.granted.set(next); }
  protected allOn(g: PermissionGroup) { return g.items.every(p => this.granted().has(p.code)); }
  protected someOn(g: PermissionGroup) { const n = g.items.filter(p => this.granted().has(p.code)).length; return n > 0 && n < g.items.length; }

  protected async save() {
    const r = this.current(); if (!r || this.locked()) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.http.put(`/api/roles/${encodeURIComponent(r.name)}/permissions`, { permissions: [...this.granted()] }));
      this.toasts.ok(`${roleLabel(r.name)} saved.${r.users ? ' It reaches the people in this role within a minute.' : ''}`);
      await this.load(r.name);
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async create() {
    const name = this.newName.trim(); if (!name) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.http.post('/api/roles', { name, copyFrom: this.copyFrom }));
      this.toasts.ok(`Role “${name}” created. Switch on what it may see and do, then save.`);
      this.newOpen.set(false); this.newName = '';
      await this.load(name); this.changed.emit();
    } catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }

  protected async remove(r: RoleRow) {
    if ((await this.confirm.ask({ title: `Delete the role “${r.name}”?`, message: 'Only a role nobody holds can be deleted.', confirmLabel: 'Delete', danger: true })) === null) return;
    this.busy.set(true);
    try { await firstValueFrom(this.http.delete(`/api/roles/${encodeURIComponent(r.name)}`)); this.toasts.ok('Role deleted.'); await this.load('CLERK'); this.changed.emit(); }
    catch (e) { this.toasts.error(messageOf(e)); } finally { this.busy.set(false); }
  }
}

function rank(name: string) { return ({ ADMIN: 0, MANAGER: 1, CLERK: 2, MARKETER: 3 } as Record<string, number>)[name] ?? 4; }
