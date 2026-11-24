import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { PocSettingsService } from '../services/poc-settings.service';

/**
 * Attaches the X-Api-Key header when configured in PocSettingsService so Strict auth mode
 * works across DirectedPageAsk and Admin dashboard endpoints.
 */
export const apiKeyInterceptor: HttpInterceptorFn = (req, next) => {
    const pocSettings = inject(PocSettingsService);
    const apiKey = pocSettings.apiKey?.trim();

    if (apiKey) {
        return next(
            req.clone({
                setHeaders: {
                    'X-Api-Key': apiKey
                }
            })
        );
    }

    return next(req);
};
