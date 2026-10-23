import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Auth } from './auth.service';
import { MARKETING_APPS } from './product-catalog';
import { APP_PERMISSION } from './permission-catalog';

/** The API rejects any state-changing call without this header (defence in depth on top of SameSite=Strict). */
export const csrfInterceptor: HttpInterceptorFn = (req, next) =>
  req.url.startsWith('/api') ? next(req.clone({ setHeaders: { 'X-Requested-With': 'inventory-ui' } })) : next(req);

/** A 401 anywhere except the sign-in call means the session ended: go back to sign-in. */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  return next(req).pipe(catchError((err: unknown) => {
    if (err instanceof HttpErrorResponse && err.status === 401 && !req.url.endsWith('/auth/login') && auth.me()) auth.clear();
    return throwError(() => err);
  }));
};

export const authGuard: CanActivateFn = () => {
  const auth = inject(Auth);
  const router = inject(Router);
  const me = auth.me();
  if (!me) return router.createUrlTree(['/login']);
  if (me.mustChangePassword) return router.createUrlTree(['/change-password']);
  return true;
};

/** The newer features are switched on per business (HasNewFeatures); where they are off, their pages send you back to the dashboard. */
export const newFeaturesGuard: CanActivateFn = () => inject(Auth).flags().hasNewFeatures ? true : inject(Router).createUrlTree(['/dashboard']);
/** Campaigns: open to a business with Marketing switched on (or all the new features). */
export const marketingGuard: CanActivateFn = () => inject(Auth).flags().hasMarketing ? true : inject(Router).createUrlTree(['/dashboard']);
/** An app page: every app with the new features; only the Marketing apps with Marketing alone. */
export const appGuard: CanActivateFn = route => {
  const f = inject(Auth).flags();
  return f.hasNewFeatures || (f.hasMarketing && MARKETING_APPS.includes(route.paramMap.get('app') ?? '')) ? true : inject(Router).createUrlTree(['/dashboard']);
};

/** Where someone lands: the first screen their role opens (the dashboard for most; Campaigns for a marketer). */
const HOMES: [string, string][] = [
  ['/dashboard', 'products.view'], ['/dashboard', 'analytics.view'], ['/sales', 'sales.view'], ['/campaigns', 'campaigns.manage'], ['/website', 'sitecontent.manage'],
  ['/customers', 'customers.view'], ['/finance', 'finance.view'], ['/team', 'attendance.view'], ['/users', 'users.manage'],
];
export function homePath(auth: Auth): string {
  const app = Object.entries(APP_PERMISSION).find(([, p]) => auth.can(p));
  return HOMES.find(([, p]) => auth.can(p))?.[0] ?? (app ? '/apps/' + app[0] : '/appearance');
}

/** A page that needs a permission: data: { perm: 'sales.view' } (or several, any one is enough: 'a|b'). Without it you go to your own first screen. */
export const permGuard: CanActivateFn = route => {
  const auth = inject(Auth);
  const wanted = String(route.data['perm'] ?? '').split('|').filter(Boolean);
  if (wanted.some(p => auth.can(p))) return true;
  const home = homePath(auth);
  return inject(Router).createUrlTree([home === '/' + route.routeConfig?.path ? '/appearance' : home]);
};

/** An app page needs that app's own permission. */
export const appPermGuard: CanActivateFn = route => {
  const auth = inject(Auth);
  return auth.can(APP_PERMISSION[route.paramMap.get('app') ?? ''] ?? '') ? true : inject(Router).createUrlTree([homePath(auth)]);
};

/** The dashboard: the management overview or the counter view, whichever the role has; otherwise your own first screen. */
export const dashboardGuard: CanActivateFn = () => {
  const auth = inject(Auth);
  return auth.can('products.view') || auth.can('analytics.view') ? true : inject(Router).createUrlTree([homePath(auth)]);
};