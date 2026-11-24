import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface BudgetInfo {
    currentUsageUsd: number;
    dailyLimitUsd: number;
    warningThresholdUsd: number;
    percentageUsed: number;
    status: string;
    providerName: string;
    date: string;
}

export interface DayCost {
    date: string;
    totalCostUsd: number;
}

export interface OperationCost {
    count: number;
    costUsd: number;
}

export interface UsageLog {
    id: number;
    operationType: string;
    modelName: string;
    inputTokens: number;
    outputTokens: number;
    cachedTokens?: number;
    costUsd: number;
    createdAt: string;
}

@Injectable({
    providedIn: 'root'
})
export class LlmCostService {
    private readonly http = inject(HttpClient);
    private readonly baseUrl = '/api/llm-costs';

    getBudgetStatus(): Observable<BudgetInfo> {
        return this.http.get<BudgetInfo>(`${this.baseUrl}/budget-status`);
    }

    getDailyTotal(date?: string): Observable<DayCost> {
        let params = new HttpParams();
        if (date) params = params.set('date', date);
        return this.http.get<DayCost>(`${this.baseUrl}/daily-total`, { params });
    }

    getCostByOperation(date?: string): Observable<{ [key: string]: OperationCost }> {
        let params = new HttpParams();
        if (date) params = params.set('date', date);
        return this.http.get<{ [key: string]: OperationCost }>(`${this.baseUrl}/by-operation`, { params });
    }

    getTrend(days: number = 7): Observable<{ date: string, costUsd: number }[]> {
        let params = new HttpParams().set('days', days);
        return this.http.get<{ date: string, costUsd: number }[]>(`${this.baseUrl}/trend`, { params });
    }

    getRecentLogs(count: number = 50): Observable<UsageLog[]> {
        let params = new HttpParams().set('count', count);
        return this.http.get<UsageLog[]>(`${this.baseUrl}/recent-logs`, { params });
    }
}
