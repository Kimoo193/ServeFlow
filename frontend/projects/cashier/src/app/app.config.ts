import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { API_CONFIG, APP_ID, authErrorInterceptor, authInterceptor } from 'shared';

import { routes } from './app.routes';
import { environment } from '../environments/environment';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor, authErrorInterceptor])),
    { provide: API_CONFIG, useValue: { baseUrl: environment.apiBaseUrl } },
    { provide: APP_ID, useValue: 'cashier' },
  ],
};
