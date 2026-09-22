import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Icon } from './icon';
import { Modal } from './modal';

// ---------------------------------------------------------------- toasts
export interface Toast { id: number; kind: 'ok' | 'bad' | 'info'; text: string; }

@Injectable({ providedIn: 'root' })
export class Toasts {
  readonly list = signal<Toast[]>([]);
  private n = 0;
  ok(text: string) { this.push('ok', text, 4000); }
  info(text: string) { this.push('info', text, 4000); }
  /** Errors stay until dismissed: they say what to fix. */
  error(text: string) { this.push('bad', text, 0); }
  dismiss(id: number) { this.list.update(l => l.filter(t => t.id !== id)); }
  private push(kind: Toast['kind'], text: string, ms: number) {
    const id = ++this.n;
    this.list.update(l => [...l, { id, kind, text }]);
    if (ms) setTimeout(() => this.dismiss(id), ms);
  }
}

@Component({
  selector: 'app-toasts',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stack" role="region" aria-label="Notifications" aria-live="polite">
      @for (t of toasts.list(); track t.id) {
        <div class="toast" [class]="t.kind" [attr.role]="t.kind === 'bad' ? 'alert' : 'status'">
          <app-icon [name]="t.kind === 'bad' ? 'alert' : 'check'" [size]="18" />
          <span>{{ t.text }}</span>
          <button type="button" class="x" (click)="toasts.dismiss(t.id)" aria-label="Dismiss"><app-icon name="close" [size]="16" /></button>
        </div>
      }
    </div>`,
  styles: `
    .stack { position: fixed; right: 1rem; bottom: 1rem; display: flex; flex-direction: column; gap: .5rem; z-index: 100; width: min(26rem, calc(100vw - 2rem)); }
    .toast { display: flex; gap: .6rem; align-items: flex-start; padding: .7rem .8rem; border-radius: var(--r-2); background: var(--ink); color: #fff; box-shadow: var(--shadow-2); animation: rise .16s ease-out; }
    .toast.ok { border-left: 5px solid #4fd08c; } .toast.bad { border-left: 5px solid #ff7b6e; background: #3a1512; } .toast.info { border-left: 5px solid var(--signal); }
    .toast span { flex: 1; font-size: .875rem; }
    .x { background: none; border: 0; color: inherit; opacity: .7; cursor: pointer; padding: 0; } .x:hover { opacity: 1; }
    @keyframes rise { from { opacity: 0; transform: translateY(6px); } }
  `,
})
export class ToastHost { protected readonly toasts = inject(Toasts); }

// ---------------------------------------------------------------- confirm
export interface ConfirmOptions { title: string; message: string; confirmLabel: string; danger?: boolean; reason?: { label: string; required: boolean }; }

@Injectable({ providedIn: 'root' })
export class Confirm {
  readonly options = signal<ConfirmOptions | null>(null);
  private resolver: ((v: string | null) => void) | null = null;

  /** Resolves with the reason text ('' if none asked), or null when cancelled. */
  ask(o: ConfirmOptions): Promise<string | null> {
    this.options.set(o);
    return new Promise(res => (this.resolver = res));
  }
  settle(v: string | null) { this.options.set(null); this.resolver?.(v); this.resolver = null; }
}

@Component({
  selector: 'app-confirm',
  imports: [Modal, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let o = svc.options();
    <app-modal [open]="!!o" [heading]="o?.title ?? ''" (closed)="cancel()">
      @if (o) {
        <p>{{ o.message }}</p>
        @if (o.reason) {
          <div class="field" style="margin-top:.9rem">
            <label for="cf-reason">{{ o.reason.label }}</label>
            <textarea id="cf-reason" class="input" [(ngModel)]="reason" rows="3"></textarea>
          </div>
        }
      }
      <ng-container modal-actions>
        <button type="button" class="btn" (click)="cancel()">Keep as is</button>
        <button type="button" class="btn" [class.btn-danger]="o?.danger" [class.btn-primary]="!o?.danger"
                [disabled]="!!o?.reason?.required && !reason.trim()" (click)="ok()">{{ o?.confirmLabel }}</button>
      </ng-container>
    </app-modal>`,
})
export class ConfirmHost {
  protected readonly svc = inject(Confirm);
  protected reason = '';
  protected cancel() { this.reason = ''; this.svc.settle(null); }
  protected ok() { const r = this.reason.trim(); this.reason = ''; this.svc.settle(r); }
}
