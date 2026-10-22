import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../../core/api.service';
import { Confirm, Toasts } from '../../shared/feedback';

interface Review { id: number; name: string; place: string | null; rating: number; text: string; consentToPublish: boolean; status: string; createdAt: string; decidedBy: string | null; }
interface Partner { id: number; name: string; url: string | null; permissionGiven: boolean; isActive: boolean; sortOrder: number; }

/** The public website's content that staff look after: reviews visitors leave (nothing shows until approved) and partner logos (only with permission). */
@Component({
  selector: 'app-website',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Website</h1><div class="actions"><a class="btn" href="/" target="_blank" rel="noopener">Open the website</a></div></div>
      <p class="muted lede">What visitors to the website see from you, besides your products. Opening hours and the WhatsApp reply promise are under
        <a routerLink="/company">Company &amp; documents</a>; product descriptions and feeding guides on each <a routerLink="/products">product</a>; delivery areas, fees and times under
        <a routerLink="/company">Company &amp; documents → Delivery zones</a>; articles in the <a routerLink="/apps/blog">Blog</a>.</p>

      <section class="card card-pad">
        <div class="head"><h2 class="display sub">Reviews</h2>
          <div class="tabs" role="tablist">
            @for (s of statuses; track s) { <button type="button" role="tab" class="tab" [class.on]="status() === s" [attr.aria-selected]="status() === s" (click)="setStatus(s)">{{ s }}</button> }
          </div></div>
        <p class="muted sm">Visitors write these on the website and agree to publication. Nothing appears until you approve it; only the first name is shown.</p>
        @for (r of reviews(); track r.id) {
          <article class="review">
            <div><strong>{{ r.name }}</strong>@if (r.place) { <span class="muted"> · {{ r.place }}</span> } <span class="stars" [attr.aria-label]="r.rating + ' out of 5'">{{ stars(r.rating) }}</span>
              <span class="muted sm"> · {{ r.createdAt.slice(0, 10) }}</span></div>
            <p>{{ r.text }}</p>
            <div class="row">
              @if (r.status !== 'Approved') { <button type="button" class="btn btn-sm btn-primary" (click)="decide(r, true)">Approve</button> }
              @if (r.status !== 'Rejected') { <button type="button" class="btn btn-sm" (click)="decide(r, false)">{{ r.status === 'Approved' ? 'Take down' : 'Reject' }}</button> }
              @if (r.decidedBy) { <span class="muted sm">{{ r.status }} by {{ r.decidedBy }}</span> }
            </div>
          </article>
        } @empty { <div class="empty"><strong>No {{ status().toLowerCase() }} reviews</strong>{{ status() === 'Pending' ? 'New reviews from the website arrive here.' : '' }}</div> }
      </section>

      <section class="card card-pad">
        <h2 class="display sub">Partner logos</h2>
        <p class="muted sm">Vets, retailers or suppliers who agreed to appear on your website. A logo is shown only while "Permission given" and "Show" are both ticked.</p>
        <div class="table-wrap"><table class="table">
          <thead><tr><th>Logo</th><th>Partner</th><th>Website</th><th>Permission given</th><th>Show</th><th>Order</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            @for (p of partners(); track p.id) {
              <tr>
                <td><img class="logo" [src]="'/api/website/partners/' + p.id + '/logo?v=' + stamp()" [alt]="p.name + ' logo'" /></td>
                <td><input class="input" [value]="p.name" maxlength="100" [attr.aria-label]="'Name of ' + p.name" (input)="patch(p, { name: $any($event.target).value })" /></td>
                <td><input class="input" [value]="p.url ?? ''" maxlength="300" placeholder="https://" [attr.aria-label]="'Website of ' + p.name" (input)="patch(p, { url: $any($event.target).value })" /></td>
                <td><input type="checkbox" [checked]="p.permissionGiven" [attr.aria-label]="'Permission given by ' + p.name" (change)="patch(p, { permissionGiven: $any($event.target).checked })" /></td>
                <td><input type="checkbox" [checked]="p.isActive" [attr.aria-label]="'Show ' + p.name" (change)="patch(p, { isActive: $any($event.target).checked })" /></td>
                <td><input class="input num w-ord" type="number" [value]="p.sortOrder" [attr.aria-label]="'Order of ' + p.name" (input)="patch(p, { sortOrder: +$any($event.target).value })" /></td>
                <td class="actions">
                  <label class="btn btn-sm">New logo<input type="file" accept="image/png,image/jpeg" hidden (change)="save(p, $any($event.target).files?.[0])" /></label>
                  @if (dirty().includes(p.id)) { <button type="button" class="btn btn-sm btn-primary" (click)="save(p)">Save</button> }
                  <button type="button" class="btn btn-sm btn-danger" (click)="remove(p)">Remove</button>
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        @if (!partners().length) { <div class="empty"><strong>No partners yet</strong>Add a partner whose logo you have permission to show.</div> }
        <form class="add" (submit)="add(pn.value, pu.value, pp.checked, pf.files?.[0]); $event.preventDefault()">
          <div class="field"><label for="pn">Partner name</label><input #pn id="pn" class="input" maxlength="100" /></div>
          <div class="field"><label for="pu">Their website (optional)</label><input #pu id="pu" class="input" maxlength="300" placeholder="https://" /></div>
          <div class="field"><label for="pf">Logo (PNG or JPEG)</label><input #pf id="pf" class="input" type="file" accept="image/png,image/jpeg" /></div>
          <label class="check"><input #pp type="checkbox" /> They gave permission</label>
          <button type="submit" class="btn">Add partner</button>
        </form>
      </section>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 56rem; } .sub { font-size: 1.3rem; margin: 0 0 .4rem; } .sm { font-size: .8rem; } section { margin-bottom: 1rem; }
    .head { display: flex; justify-content: space-between; align-items: center; gap: 1rem; flex-wrap: wrap; }
    .tabs { display: flex; gap: .3rem; } .tab { border: 1px solid var(--line); background: #fff; border-radius: 99px; padding: .3rem .8rem; cursor: pointer; } .tab.on { background: var(--ink, #111); color: #fff; }
    .review { border-top: 1px solid var(--line); padding: .8rem 0; } .review p { margin: .3rem 0 .5rem; white-space: pre-wrap; } .stars { color: #d9a441; margin-left: .3rem; letter-spacing: .1em; }
    .row { display: flex; gap: .5rem; align-items: center; flex-wrap: wrap; }
    .logo { width: 3.5rem; height: 2.2rem; object-fit: contain; } .w-ord { width: 5rem; } .actions { white-space: nowrap; }
    .add { display: flex; gap: .6rem; align-items: end; flex-wrap: wrap; margin-top: 1rem; } .check { display: inline-flex; gap: .4rem; align-items: center; padding-bottom: .5rem; }
  `,
})
export class WebsitePage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(Toasts);
  private readonly confirm = inject(Confirm);
  protected readonly statuses = ['Pending', 'Approved', 'Rejected'];
  protected readonly status = signal('Pending');
  protected readonly reviews = signal<Review[]>([]);
  protected readonly partners = signal<Partner[]>([]);
  protected readonly dirty = signal<number[]>([]);
  protected readonly stamp = signal(Date.now());

  ngOnInit() { void this.loadReviews(); void this.loadPartners(); }

  protected stars(n: number) { return '★'.repeat(n) + '☆'.repeat(5 - n); }
  protected setStatus(s: string) { this.status.set(s); void this.loadReviews(); }
  private async loadReviews() {
    try { this.reviews.set(await firstValueFrom(this.http.get<Review[]>('/api/website/reviews', { params: { status: this.status() } }))); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async decide(r: Review, approve: boolean) {
    try { await firstValueFrom(this.http.post(`/api/website/reviews/${r.id}/${approve ? 'approve' : 'reject'}`, {})); this.toasts.ok(approve ? 'Review published.' : 'Review hidden.'); await this.loadReviews(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }

  private async loadPartners() {
    try { this.partners.set(await firstValueFrom(this.http.get<Partner[]>('/api/website/partners'))); this.dirty.set([]); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected patch(p: Partner, change: Partial<Partner>) {
    this.partners.update(ps => ps.map(x => (x.id === p.id ? { ...x, ...change } : x)));
    this.dirty.update(d => (d.includes(p.id) ? d : [...d, p.id]));
  }
  private form(p: { name: string; url: string | null; permissionGiven: boolean; isActive: boolean; sortOrder: number }, file?: File) {
    const f = new FormData();
    f.set('name', p.name.trim()); f.set('url', (p.url ?? '').trim()); f.set('permissionGiven', String(p.permissionGiven)); f.set('isActive', String(p.isActive)); f.set('sortOrder', String(p.sortOrder || 0));
    if (file) f.set('logo', file);
    return f;
  }
  protected async save(p: Partner, file?: File) {
    const cur = this.partners().find(x => x.id === p.id) ?? p;
    try { await firstValueFrom(this.http.put(`/api/website/partners/${p.id}`, this.form(cur, file))); this.toasts.ok(`${cur.name} saved.`); this.stamp.set(Date.now()); await this.loadPartners(); }
    catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async add(name: string, url: string, permission: boolean, file?: File) {
    if (!name.trim()) { this.toasts.error('Enter the partner’s name.'); return; }
    if (!file) { this.toasts.error('Choose the partner’s logo.'); return; }
    try {
      await firstValueFrom(this.http.post('/api/website/partners', this.form({ name, url, permissionGiven: permission, isActive: true, sortOrder: this.partners().length }, file)));
      this.toasts.ok(`${name.trim()} added.`); await this.loadPartners();
    } catch (e) { this.toasts.error(messageOf(e)); }
  }
  protected async remove(p: Partner) {
    const ok = await this.confirm.ask({ title: `Remove ${p.name}?`, message: 'Their logo disappears from the website.', confirmLabel: 'Remove', danger: true });
    if (ok === null) return;
    try { await firstValueFrom(this.http.delete(`/api/website/partners/${p.id}`)); await this.loadPartners(); } catch (e) { this.toasts.error(messageOf(e)); }
  }
}
