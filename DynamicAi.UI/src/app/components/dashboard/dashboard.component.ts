import { Component, OnInit, inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BaseChartDirective } from 'ng2-charts';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { Chart, ChartConfiguration, ChartOptions, registerables } from 'chart.js';
import { MetricsService } from '../../services/metrics.service';
import {
    PerformanceMetrics,
    IntentDistribution,
    CostBreakdown,
    UserExperienceMetrics,
    ProblematicQuery
} from '../../models';

// Register Chart.js components
Chart.register(...registerables);

@Component({
    selector: 'app-dashboard',
    standalone: true,
    imports: [CommonModule, BaseChartDirective],
    templateUrl: './dashboard.component.html',
    styleUrls: ['./dashboard.component.css']
})
export class DashboardComponent implements OnInit {
    private metricsService = inject(MetricsService);
    private cdr = inject(ChangeDetectorRef);

    performance: PerformanceMetrics | null = null;
    experience: UserExperienceMetrics | null = null;
    costs: CostBreakdown | null = null;
    intents: IntentDistribution[] = [];
    problematicQueries: ProblematicQuery[] = [];

    // Charts Config
    public lineChartData: ChartConfiguration<'line'>['data'] = { datasets: [], labels: [] };
    public lineChartOptions: ChartOptions<'line'> = { responsive: true };
    public lineChartLegend = true;

    public pieChartData: ChartConfiguration<'pie'>['data'] = { labels: [], datasets: [{ data: [] }] };
    public pieChartOptions: ChartOptions<'pie'> = { responsive: true };

    public barChartData: ChartConfiguration<'bar'>['data'] = { labels: [], datasets: [] };

    loading = true;

    ngOnInit() {
        this.loadData();
    }

    loadData() {
        this.loading = true;
        console.log('[Dashboard] Loading data...');

        // Use forkJoin with catchError per observable to prevent silent failures
        // Using 30d period to show historical data
        forkJoin({
            performance: this.metricsService.getPerformanceMetrics('30d').pipe(
                catchError((err: any) => { console.error('[Dashboard] Performance error:', err); return of(null); })
            ),
            experience: this.metricsService.getUserExperience('30d').pipe(
                catchError((err: any) => { console.error('[Dashboard] Experience error:', err); return of(null); })
            ),
            costs: this.metricsService.getCostBreakdown('30d').pipe(
                catchError((err: any) => { console.error('[Dashboard] Costs error:', err); return of(null); })
            ),
            intents: this.metricsService.getIntentDistribution('30d').pipe(
                catchError((err: any) => { console.error('[Dashboard] Intents error:', err); return of([] as IntentDistribution[]); })
            ),
            queries: this.metricsService.getProblematicQueries().pipe(
                catchError((err: any) => { console.error('[Dashboard] Queries error:', err); return of([] as ProblematicQuery[]); })
            )
        }).subscribe({
            next: (results) => {
                console.log('[Dashboard] Data received:', results);

                this.performance = results.performance;
                if (results.performance) {
                    this.updatePerformanceChart(results.performance);
                }

                this.experience = results.experience;
                this.costs = results.costs;

                this.intents = results.intents || [];
                this.updateIntentChart(this.intents);

                this.problematicQueries = results.queries || [];
                this.loading = false;
                console.log('[Dashboard] Loading complete. loading =', this.loading);

                // Force Angular to detect changes
                this.cdr.detectChanges();
            },
            error: (err: any) => {
                console.error('[Dashboard] Failed to load metrics', err);
                this.loading = false;
            }
        });
    }

    updatePerformanceChart(data: PerformanceMetrics) {
        if (!data) return;

        this.lineChartData = {
            labels: ['P95 Latency', 'Avg Latency'],
            datasets: [
                {
                    data: [data.p95LatencyMs || 0, data.avgLatencyMs || 0],
                    label: 'Latency (ms)',
                    fill: true,
                    tension: 0.5,
                    borderColor: 'rgba(75,192,192,1)',
                    backgroundColor: 'rgba(75,192,192,0.2)'
                }
            ]
        };
    }

    /** Palette for Intent Distribution (Phase 1: DataRetrieval, Analysis, Prediction, etc.) */
    private readonly intentChartColors = [
        '#FF6384', '#36A2EB', '#FFCE56', '#4BC0C0', '#9966FF', '#FF9F40', '#E0E0E0'
    ];

    updateIntentChart(data: IntentDistribution[]) {
        if (!data || data.length === 0) {
            this.pieChartData = {
                labels: ['No Data'],
                datasets: [{
                    data: [1],
                    backgroundColor: ['#E0E0E0']
                }]
            };
            return;
        }

        const colors = data.map((_, i) => this.intentChartColors[i % this.intentChartColors.length]);
        this.pieChartData = {
            labels: data.map(d => d.intent),
            datasets: [{
                data: data.map(d => d.count),
                backgroundColor: colors
            }]
        };
    }

    /**
     * Calculate dislike rate as percentage of total feedback
     */
    getDislikeRate(): number {
        if (!this.experience) return 0;
        const total = this.experience.likeCount + this.experience.dislikeCount;
        if (total === 0) return 0;
        return (this.experience.dislikeCount / total) * 100;
    }
}
