import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../../core/api.service';
import { Toasts } from '../../shared/feedback';
import { Modal } from '../../shared/modal';

interface Branch { id: number; code: string; name: string; address: string | null; state: string | null; phone: string | null; isHeadOffice: boolean; active: boolean; warehouses: { id: number; name: string }[]; }
interface BranchUser { userId: number; username: string; fullName: string; branchId: number | null; }
interface Perf { branchId: number; branch: string; sales: number; collected: number; income: number; expenses: number; netProfit: number; stockValue: number; }
interface Form { id: number | null; code: string; name: string; address: string; state: string; phone: string; active: boolean; }

/** Branches: set them up, put each warehouse and each staff member in one, and see how each is doing this year. */
@Component({
  selector: 'app-branches',
  imports: [FormsModule, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Branches</h1><div class="grow"></div><button type="button" class="btn btn-primary" (click)="start(null)">New branch</button></div>

      <section class="card">
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Branch</th><th>Warehouses</th><th class="num">Sales this year</th><th class="num">Collected</th><th class="num">Profit this year</th><th class="num">Stock at cost</th><th></th></tr></thead>
          <tbody>@for (b of branches(); track b.id) {
            <tr [class.muted]="!b.active">
              <td><span class="strong">{{ b.name }}</span> <span class="muted sm">{{ b.code }}</span>@if (b.isHeadOffice) { <span class="stamp stamp-info">Head office</span> }@if (!b.active) { <span class="stamp stamp-warn">Closed</span> }
                <div class="muted sm">{{ b.address }}{{ b.state ? ', ' + b.state : '' }}{{ b.phone ? ' · ' + b.phone : '' }}</div></td>
              <td>@for (w of b.warehouses; track w.id) { <div class="wh">{{ w.name }}
                  <select class="input sm-input" [attr.aria-label]="'Move ' + w.name" [ngModel]="b.id" (ngModelChange)="moveWarehouse(w.id, $event)">@for (o of open(); track o.id) { <option [ngValue]="o.id">{{ o.name }}</option> }</select></div> }
                @empty { <span class="muted sm">No warehouses</span> }</td>
              <td class="num">{{ money(perf(b.id)?.sales) }}</td><td class="num">{{ money(perf(b.id)?.collected) }}</td><td class="num strong">{{ money(perf(b.id)?.netProfit) }}</td><td class="num">{{ money(perf(b.id)?.stockValue) }}</td>
              <td><button type="button" class="btn btn-sm" (click)="start(b)">Edit</button></td></tr>
          }</tbody>
        </table></div>
      </section>

      <section class="card">
        <h2 class="display">Staff</h2>
        <p class="muted sm">A person tied to a branch can sell and move stock only from that branch's warehouses. Leave it on "Every branch" for owners and managers. It takes effect the next time they sign in.</p>
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Name</th><th>Username</th><th>Works at</th></tr></thead>
          <tbody>@for (u of users(); track u.userId) {
            <tr><td>{{ u.fullName }}</td><td class="muted">{{ u.username }}</td>
              <td><select class="input" [attr.aria-label]="'Branch for ' + u.fullName" [ngModel]="u.branchId" (ngModelChange)="assignUser(u.userId, $event)"><option [ngValue]="null">Every branch</option>@for (o of open(); track o.id) { <option [ngValue]="o.id">{{ o.name }}</option> }</select></td></tr>
          }</tbody>
        </table></div>
      </section>
    </div>

    <app-modal [open]="editing()" [heading]="f.id ? 'Edit branch' : 'New branch'" (closed)="editing.set(false)">
      <form id="bf" class="form-grid" (ngSubmit)="save()">
        <div class="field"><label for="bc">Short code</label><input id="bc" class="input" name="bc" [(ngModel)]="f.code" maxlength="10" placeholder="IKJ" required /></div>
        <div class="field"><label for="bn">Name</label><input id="bn" class="input" name="bn" [(ngModel)]="f.name" required /></div>
        <div class="field span-2"><label for="ba">Address</label><input id="ba" class="input" name="ba" [(ngModel)]="f.address" /></div>
        <div class="field"><label for="bs">State</label><input id="bs" class="input" name="bs" [(ngModel)]="f.state" /></div>
        <div class="field"><label for="bp">Phone</label><input id="bp" class="input" name="bp" [(ngModel)]="f.phone" /></div>
        @if (f.id) { <label class="field span-2"><input type="checkbox" name="bo" [(ngModel)]="f.active" /> Open</label> }
      </form>
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="editing.set(false)">Cancel</button>
        <button type="submit" form="bf" class="btn btn-primary" [disabled]="!f.code.trim() || !f.name.trim()">Save</button>
      </ng-container>
    </app-modal>`,
  styles: `.page-head { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; } .card { margin-bottom: 1rem; } .sm { font-size: .78rem; }
    .wh { display: flex; gap: .5rem; align-items: center; margin-bottom: .25rem; } .sm-input { max-width: 11rem; padding: .2rem .4rem; }`,
})
export class BranchesPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  protected readonly branches = signal<Branch[]>([]);
  protected readonly users = signal<BranchUser[]>([]);
  protected readonly perfs = signal<Perf[]>([]);
  protected readonly editing = signal(false);
  protected f: Form = this.blank();

  protected open() { return this.branches().filter(b => b.active); }
  protected perf(id: number) { return this.perfs().find(p => p.branchId === id); }
  protected money(n: number | undefined) { return n === undefined ? '' : '₦' + n.toLocaleString('en-NG', { maximumFractionDigits: 0 }); }
  private blank(): Form { return { id: null, code: '', name: '', address: '', state: '', phone: '', active: true }; }

  ngOnInit() { void this.load(); }
  private async load() {
    try {
      this.branches.set(await firstValueFrom(this.http.get<Branch[]>('/api/branches')));
      this.users.set(await firstValueFrom(this.http.get<BranchUser[]>('/api/branches/users')));
      this.perfs.set(await firstValueFrom(this.http.get<Perf[]>('/api/branches/performance')));
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected start(b: Branch | null) {
    this.f = b ? { id: b.id, code: b.code, name: b.name, address: b.address ?? '', state: b.state ?? '', phone: b.phone ?? '', active: b.active } : this.blank();
    this.editing.set(true);
  }
  protected async save() {
    const body = { code: this.f.code, name: this.f.name, address: this.f.address, state: this.f.state, phone: this.f.phone, active: this.f.active };
    try {
      if (this.f.id) await firstValueFrom(this.http.put(`/api/branches/${this.f.id}`, body)); else await firstValueFrom(this.http.post('/api/branches', body));
      this.editing.set(false); this.toasts.ok('Saved.'); await this.load();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async moveWarehouse(warehouseId: number, branchId: number) {
    try { await firstValueFrom(this.http.put(`/api/branches/warehouses/${warehouseId}`, { branchId })); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); await this.load(); }
  }
  protected async assignUser(userId: number, branchId: number | null) {
    try { await firstValueFrom(this.http.put(`/api/branches/users/${userId}`, { branchId })); this.toasts.ok('Saved. It applies at their next sign-in.'); await this.load(); } catch (e) { this.toasts.error(messageOf(e)); await this.load(); }
  }
}
