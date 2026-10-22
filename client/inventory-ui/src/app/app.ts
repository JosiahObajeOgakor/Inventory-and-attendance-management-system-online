import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NavigationCancel, NavigationEnd, NavigationError, NavigationStart, Router, RouterOutlet } from '@angular/router';
import { Loading } from './core/loading.service';
import { ConfirmHost, ToastHost } from './shared/feedback';
import { Loader } from './shared/loader';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastHost, ConfirmHost, Loader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The loader sits over everything (sign-in included); toasts stay above it so errors are still readable.
  template: `<router-outlet /><app-loader /><app-toasts /><app-confirm />`,
})
export class App {
  constructor() {
    // Opening a screen for the first time downloads its code; on a slow connection that counts as waiting too.
    const loading = inject(Loading);
    let navigating = false;
    inject(Router).events.subscribe(e => {
      if (e instanceof NavigationStart && !navigating) { navigating = true; loading.begin(); }
      else if ((e instanceof NavigationEnd || e instanceof NavigationCancel || e instanceof NavigationError) && navigating) { navigating = false; loading.end(); }
    });
  }
}
