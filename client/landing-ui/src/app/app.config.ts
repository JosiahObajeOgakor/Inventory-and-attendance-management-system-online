import { ApplicationConfig, PLATFORM_ID, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { csrfInterceptor } from './core/http';
import { setWhatsappNumber } from './core/whatsapp';
import { Pixels, TrackingIds } from './core/pixels';
import { provideClientHydration } from '@angular/platform-browser';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withFetch(), withInterceptors([csrfInterceptor])),
    // Before the page is built, ask the server which WhatsApp number the sales assistant answers on, so every link points at it.
    provideAppInitializer(() => {
      if (!isPlatformBrowser(inject(PLATFORM_ID))) return;
      const pixels = inject(Pixels);
      return firstValueFrom(inject(HttpClient).get<{ whatsappNumber?: string | null; tracking?: TrackingIds | null }>('/api/webchat/status'))
        .then(s => { setWhatsappNumber(s.whatsappNumber); pixels.configure(s.tracking); }, () => { /* keep the default number */ });
    }),
    // The page is pre-rendered at build time: the hero video is in the HTML, so it starts loading and playing before any script runs.
    // No event replay — it needs an inline script, which the site's Content-Security-Policy (script-src 'self') blocks.
    provideClientHydration(),
  ],
};
