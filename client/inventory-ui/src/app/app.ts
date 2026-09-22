import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ConfirmHost, ToastHost } from './shared/feedback';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastHost, ConfirmHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<router-outlet /><app-toasts /><app-confirm />`,
})
export class App {}
