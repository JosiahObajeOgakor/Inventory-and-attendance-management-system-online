import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Auth } from './auth.service';

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

export const adminGuard: CanActivateFn = () => {
  const auth = inject(Auth);
  return auth.isAdmin() ? true : inject(Router).createUrlTree(['/dashboard']);
};
