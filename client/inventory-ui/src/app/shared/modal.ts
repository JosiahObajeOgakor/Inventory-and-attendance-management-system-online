import { ChangeDetectionStrategy, Component, ElementRef, effect, input, output, viewChild } from '@angular/core';
import { Icon } from './icon';

/**
 * A modal on the native <dialog> element: focus is trapped, Esc closes, and the page behind is inert without any extra code.
 * The parent owns the `open` flag and clears it in (closed).
 */
@Component({
  selector: 'app-modal',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dialog #dlg class="modal" [class.wide]="wide()" (cancel)="onCancel($event)" (click)="onBackdrop($event)" [attr.aria-labelledby]="'m-title'">
      <div class="head">
        <h2 id="m-title" class="display">{{ heading() }}</h2>
        <button type="button" class="btn btn-quiet btn-icon" (click)="closed.emit()" aria-label="Close"><app-icon name="close" /></button>
      </div>
      <div class="body"><ng-content /></div>
      <div class="foot"><ng-content select="[modal-actions]" /></div>
    </dialog>`,
  styles: `
    .head { display: flex; align-items: center; justify-content: space-between; padding: 1rem 1.25rem .75rem; border-bottom: 1px solid var(--line); }
    h2 { font-size: 1.6rem; }
    .body { padding: 1.1rem 1.25rem; overflow: auto; max-height: calc(100dvh - 12rem); }
    .foot { display: flex; justify-content: flex-end; gap: .5rem; padding: .85rem 1.25rem; border-top: 1px solid var(--line); background: #f3f6f4; border-radius: 0 0 var(--r-2) var(--r-2); }
    .foot:empty { display: none; }
  `,
})
export class Modal {
  readonly open = input(false);
  readonly heading = input('');
  readonly wide = input(false);
  readonly closed = output<void>();
  private readonly dlg = viewChild.required<ElementRef<HTMLDialogElement>>('dlg');

  constructor() {
    effect(() => {
      const el = this.dlg().nativeElement;
      if (this.open() && !el.open) el.showModal();
      else if (!this.open() && el.open) el.close();
    });
  }

  protected onCancel(e: Event): void { e.preventDefault(); this.closed.emit(); }
  protected onBackdrop(e: MouseEvent): void { if (e.target === this.dlg().nativeElement) this.closed.emit(); }
}
