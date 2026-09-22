import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../../core/api.service';
import { AuditRow, LoginAuditRow } from '../../core/models';
import { Icon } from '../../shared/icon';
import { PagedList } from '../../shared/paged-list';
import { Pager, StampTimePipe } from '../../shared/ui';

@Component({
  selector: 'app-activity',
  imports: [Icon, Pager, StampTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Activity log</h1></div>
      <div class="tabs" role="tablist">
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'actions'" (click)="setTab('actions')">What people did</button>
        <button type="button" class="tab" role="tab" [attr.aria-selected]="tab() === 'logins'" (click)="setTab('logins')">Sign-ins</button>
      </div>
      <section class="card">
        @if (tab() === 'actions') {
          <div class="toolbar"><div class="search grow"><app-icon name="search" [size]="17" />
            <input class="input" type="search" placeholder="Search person, action or record" aria-label="Search activity" (input)="actions.setSearch($any($event.target).value)" /></div></div>
          @if (actions.error()) { <p class="notice bad" style="margin:1rem" role="alert">{{ actions.error() }}</p> }
          <div class="table-wrap"><table class="table">
            <thead><tr><th>When</th><th>Who</th><th>Did</th><th>To</th><th>Detail</th></tr></thead>
            <tbody>@for (a of actions.items(); track a.id) { <tr><td>{{ a.at | stampTime }}</td><td class="strong">{{ a.user }}</td><td class="mono">{{ a.action }}</td><td>{{ a.entity }} {{ a.entityId }}</td><td>{{ a.detail }}</td></tr> }</tbody>
          </table></div>
          @if (!actions.loading() && !actions.items().length) { <div class="empty"><strong>Nothing recorded yet</strong>Sales, stock changes and admin actions are logged here.</div> }
          <app-pager [page]="actions.page()" [pageSize]="actions.pageSize" [total]="actions.total()" (pageChange)="actions.goTo($event)" />
        } @else {
          <div class="table-wrap"><table class="table">
            <thead><tr><th>When</th><th>Username</th><th>Result</th><th>Reason</th><th>From</th></tr></thead>
            <tbody>@for (l of logins.items(); track l.id) { <tr><td>{{ l.atUtc | stampTime }}</td><td class="mono">{{ l.username }}</td>
              <td>@if (l.succeeded) { <span class="stamp stamp-ok">Signed in</span> } @else { <span class="stamp stamp-bad">Refused</span> }</td><td>{{ l.reason }}</td><td class="mono">{{ l.clientAddress }}</td></tr> }</tbody>
          </table></div>
          @if (!logins.loading() && !logins.items().length) { <div class="empty"><strong>No sign-ins recorded</strong></div> }
          <app-pager [page]="logins.page()" [pageSize]="logins.pageSize" [total]="logins.total()" (pageChange)="logins.goTo($event)" />
        }
      </section>
    </div>`,
})
export class ActivityPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly tab = signal<'actions' | 'logins'>('actions');
  protected readonly actions = new PagedList<AuditRow>(q => this.api.audit(q), 20);
  protected readonly logins = new PagedList<LoginAuditRow>(q => this.api.loginAudit(q), 20);
  ngOnInit() { void this.actions.load(); }
  protected setTab(t: 'actions' | 'logins') { this.tab.set(t); void (t === 'actions' ? this.actions : this.logins).load(); }
}
