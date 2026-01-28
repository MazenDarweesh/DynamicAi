import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { DislikeRequest, DislikeResponse, LikeResponse } from '../models';

@Injectable({
    providedIn: 'root'
})
export class ApiService {
    private apiUrl = environment.apiUrl;

    constructor(private http: HttpClient) { }

    testConnection(): Observable<any> {
        return this.http.get(`${this.apiUrl}/Test/ping`);
    }

    ask(question: string, previousQuestion?: string, previousSql?: string, sessionId?: string): Observable<any> {
        const body: Record<string, unknown> = {
            question,
            tenantId: 'default',
            comID: 1,
            previousQuestion: previousQuestion ?? null,
            previousSql: previousSql ?? null
        };
        if (sessionId) body.sessionId = sessionId;
        return this.http.post(`${this.apiUrl}/Test/ask`, body);
    }

    dislike(request: DislikeRequest): Observable<DislikeResponse> {
        return this.http.post<DislikeResponse>(`${this.apiUrl}/api/Feedback/dislike`, request);
    }

    like(queryLogId: string, tenantId?: number): Observable<LikeResponse> {
        return this.http.post<LikeResponse>(`${this.apiUrl}/api/Feedback/like/${queryLogId}`, null, {
            params: tenantId ? { tenantId: tenantId.toString() } : {}
        });
    }
}

