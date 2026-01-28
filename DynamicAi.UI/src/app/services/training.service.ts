import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { TrainingStats, ManualTrainingRequest, FeedbackResponse } from '../models';

/**
 * Service for Training Data Management API
 */
@Injectable({
    providedIn: 'root'
})
export class TrainingService {
    private readonly http = inject(HttpClient);
    private readonly baseUrl = '/api/feedback/training';

    /**
     * Get training data statistics
     */
    getStats(): Observable<TrainingStats> {
        return this.http.get<TrainingStats>(`${this.baseUrl}/stats`);
    }

    /**
     * Export training data as JSONL file
     * @param verifiedOnly - Export only verified examples (default: true)
     */
    exportData(verifiedOnly: boolean = true): Observable<Blob> {
        return this.http.get(`${this.baseUrl}/export`, {
            params: { verifiedOnly: verifiedOnly.toString() },
            responseType: 'blob'
        });
    }

    /**
     * Manually add training example to Golden Dataset
     */
    addManualExample(request: ManualTrainingRequest): Observable<FeedbackResponse> {
        return this.http.post<FeedbackResponse>(`${this.baseUrl}/manual`, request);
    }
}
