import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
    IntentReviewItemDto,
    ClearIntentRequest,
    ClearCacheRequest,
    IntentReviewStats
} from '../models';

/**
 * Service for Intent Review API (Phase 1: The Brain – Feedback Loop).
 * Endpoints: api/intent-review/pending, approve, reject, stats, clear-by-question.
 */
@Injectable({
    providedIn: 'root'
})
export class IntentReviewService {
    private readonly http = inject(HttpClient);
    private readonly baseUrl = '/api/intent-review';

    getPending(): Observable<IntentReviewItemDto[]> {
        return this.http.get<IntentReviewItemDto[]>(`${this.baseUrl}/pending`);
    }

    approve(id: string): Observable<{ message: string }> {
        return this.http.post<{ message: string }>(`${this.baseUrl}/${id}/approve`, {});
    }

    reject(id: string): Observable<{ message: string }> {
        return this.http.post<{ message: string }>(`${this.baseUrl}/${id}/reject`, {});
    }

    getStats(): Observable<IntentReviewStats> {
        return this.http.get<IntentReviewStats>(`${this.baseUrl}/stats`);
    }

    clearByQuestion(request: ClearIntentRequest): Observable<{ message: string; question: string }> {
        return this.http.post<{ message: string; question: string }>(
            `${this.baseUrl}/clear-by-question`,
            request
        );
    }

    clearCacheByQuestion(request: ClearCacheRequest): Observable<{ message: string; question: string }> {
        return this.http.post<{ message: string; question: string }>(
            '/api/query-cache/clear-by-question',
            request
        );
    }

    updateIntent(id: string, intentType: string): Observable<{ message: string; intentType: string }> {
        return this.http.patch<{ message: string; intentType: string }>(
            `${this.baseUrl}/${id}/update-intent`,
            { intentType }
        );
    }

    getAvailableIntents(): Observable<string[]> {
        return this.http.get<string[]>(`${this.baseUrl}/available-intents`);
    }
}
