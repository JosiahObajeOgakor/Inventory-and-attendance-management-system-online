import { HttpInterceptorFn } from '@angular/common/http';

/** The API rejects any state-changing call without this header (same backend as inventory-ui, same rule). */
export const csrfInterceptor: HttpInterceptorFn = (req, next) =>
  req.url.startsWith('/api') ? next(req.clone({ setHeaders: { 'X-Requested-With': 'inventory-ui' } })) : next(req);
