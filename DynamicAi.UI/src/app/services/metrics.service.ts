import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import {
    PerformanceMetrics,
    IntentDistribution,
    CostBreakdown,
    UserExperienceMetrics,
    ProblematicQuery
} from '../models';

@Injectable({
    providedIn: 'root'
})
export class MetricsService {
    private readonly http = inject(HttpClient);
    // Using relative path via proxy (see proxy.conf.json)
    private readonly baseUrl = '/api/metrics';

    getPerformanceMetrics(period: string = '24h'): Observable<PerformanceMetrics> {
        const hours = this.parsePeriodToHours(period);
        return this.http.get<PerformanceMetrics>(`${this.baseUrl}/performance`, { params: { hours } });
    }

    getIntentDistribution(period: string = '24h'): Observable<IntentDistribution[]> {
        const days = this.parsePeriodToDays(period);
        return this.http.get<IntentDistribution[]>(`${this.baseUrl}/intent-distribution`, { params: { days } });
    }

    getCostBreakdown(period: string = '24h'): Observable<CostBreakdown> {
        const days = this.parsePeriodToDays(period);
        return this.http.get<CostBreakdown>(`${this.baseUrl}/cost-breakdown`, { params: { days } });
    }

    getUserExperience(period: string = '24h'): Observable<UserExperienceMetrics> {
        const days = this.parsePeriodToDays(period);
        return this.http.get<UserExperienceMetrics>(`${this.baseUrl}/user-experience`, { params: { days } });
    }

    getProblematicQueries(count: number = 10): Observable<ProblematicQuery[]> {
        return this.http.get<ProblematicQuery[]>(`${this.baseUrl}/problematic-queries`, { params: { count } });
    }

    private parsePeriodToHours(period: string): number {
        if (period === '24h') return 24;
        if (period === '7d') return 168;
        if (period === '30d') return 720;
        return 24;
    }

    private parsePeriodToDays(period: string): number {
        if (period === '24h') return 1;
        if (period === '7d') return 7;
        if (period === '30d') return 30;
        return 1;
    }
}
