import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { routes } from './app.routes';
import { Auth } from './core/auth.service';
import { Appearance } from './core/appearance.service';
import { I18n } from './core/i18n.service';
import { csrfInterceptor, sessionInterceptor } from './core/http';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ scrollPositionRestoration: 'top' })),
    provideHttpClient(withInterceptors([csrfInterceptor, sessionInterceptor])),
    // Pick up an existing session cookie before the first route renders.
    provideAppInitializer(() => inject(Auth).restore()),
    provideAppInitializer(() => inject(Appearance).init()),
    // Igbo/English: fetch the saved language's catalogue before the first screen shows.
    provideAppInitializer(() => inject(I18n).init()),
  ],
};
