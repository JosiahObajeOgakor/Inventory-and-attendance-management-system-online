import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { csrfInterceptor } from './core/http';
import { provideClientHydration } from '@angular/platform-browser';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withFetch(), withInterceptors([csrfInterceptor])),
    // The page is pre-rendered at build time: the hero video is in the HTML, so it starts loading and playing before any script runs.
    // No event replay — it needs an inline script, which the site's Content-Security-Policy (script-src 'self') blocks.
    provideClientHydration(),
  ],
};
