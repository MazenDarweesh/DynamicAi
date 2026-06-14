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

    /**
     * Get pending (unverified) training examples
     */
    getPendingExamples(): Observable<any[]> {
        return this.http.get<any[]>(`${this.baseUrl}/pending`);
    }

    /**
     * Verify (approve) a training example
     */
    verifyExample(id: string): Observable<FeedbackResponse> {
        return this.http.post<FeedbackResponse>(`${this.baseUrl}/verify/${id}`, {});
    }

    /**
     * Reject (delete) a training example
     */
    rejectExample(id: string): Observable<FeedbackResponse> {
        return this.http.delete<FeedbackResponse>(`${this.baseUrl}/${id}`);
    }

    /**
     * Get pending (unverified) column synonyms
     */
    getPendingSynonyms(): Observable<any> {
        return this.http.get<any>('/api/feedback/synonyms/pending');
    }

    /**
     * Approve a column synonym link
     */
    approveSynonym(id: string, approvedBy: string = 'admin'): Observable<any> {
        return this.http.post<any>(`/api/feedback/synonyms/${id}/approve`, {}, {
            params: { approvedBy }
        });
    }

    /**
     * Reject (delete) a column synonym link
     */
    rejectSynonym(id: string): Observable<any> {
        return this.http.delete<any>(`/api/feedback/synonyms/${id}/reject`);
    }

    /**
     * Get approved synonyms for a specific page context
     */
    getApprovedSynonymsByPage(pageId: number): Observable<any> {
        return this.http.get<any>(`/api/feedback/synonyms/approved/${pageId}`);
    }
}
