import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withRouterConfig } from '@angular/router';
import { routes } from './app.routes';
import { apiKeyInterceptor } from './interceptors/api-key.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([apiKeyInterceptor])),
    provideRouter(routes, withRouterConfig({ onSameUrlNavigation: 'reload' }))
  ]
};
