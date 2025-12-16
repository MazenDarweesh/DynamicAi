import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { DislikeRequest, DislikeResponse } from '../models';

@Injectable({
    providedIn: 'root'
})
export class ApiService {
    private apiUrl = environment.apiUrl;

    constructor(private http: HttpClient) { }

    testConnection(): Observable<any> {
        return this.http.get(`${this.apiUrl}/Test/ping`);
    }

    ask(question: string): Observable<any> {
        return this.http.post(`${this.apiUrl}/Test/ask`, {
            question: question,
            tenantId: 'default',
            comID: 1
        });
    }

    dislike(request: DislikeRequest): Observable<DislikeResponse> {
        return this.http.post<DislikeResponse>(`${this.apiUrl}/api/Feedback/dislike`, request);
    }
}
