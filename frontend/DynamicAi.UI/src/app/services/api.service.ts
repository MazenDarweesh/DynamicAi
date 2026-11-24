import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { DirectedPageAskRequest, DislikeRequest, DislikeResponse, LikeResponse } from '../models';

@Injectable({
    providedIn: 'root'
})
export class ApiService {
    private apiUrl = environment.apiUrl;

    constructor(private http: HttpClient) { }

    testConnection(): Observable<any> {
        return this.http.get(`${this.apiUrl}/AIQuery/ping`);
    }

    directedPageAsk(request: DirectedPageAskRequest): Observable<any> {
        return this.http.post(`${this.apiUrl}/AIQuery/integration/directed-page-ask`, request);
    }

    clearCacheByPage(pageId: number): Observable<any> {
        return this.http.delete(`${this.apiUrl}/api/query-cache/page/${pageId}`);
    }

    clearCacheByQuestion(request: { question: string; tenantId?: string; companyId?: number; pageId?: number }): Observable<any> {
        return this.http.post(`${this.apiUrl}/api/query-cache/clear-by-question`, request);
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
