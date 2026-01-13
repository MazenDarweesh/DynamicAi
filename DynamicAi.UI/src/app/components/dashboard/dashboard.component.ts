import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BaseChartDirective } from 'ng2-charts';
import { forkJoin } from 'rxjs';
import { MetricsService } from '../../services/metrics.service';
import {
    PerformanceMetrics,
    IntentDistribution,
    CostBreakdown,
    UserExperienceMetrics,
    ProblematicQuery
} from '../../models';
import { ChartConfiguration, ChartOptions, ChartType } from 'chart.js';

@Component({
    selector: 'app-dashboard',
    standalone: true,
    imports: [CommonModule, BaseChartDirective],
    templateUrl: './dashboard.component.html',
    styleUrls: ['./dashboard.component.css']
})
export class DashboardComponent implements OnInit {
    private metricsService = inject(MetricsService);

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

        // Use forkJoin to wait for all requests to complete
        forkJoin({
            performance: this.metricsService.getPerformanceMetrics(),
            experience: this.metricsService.getUserExperience(),
            costs: this.metricsService.getCostBreakdown(),
            intents: this.metricsService.getIntentDistribution(),
            queries: this.metricsService.getProblematicQueries()
        }).subscribe({
            next: (results) => {
                this.performance = results.performance;
                this.updatePerformanceChart(results.performance);

                this.experience = results.experience;
                this.costs = results.costs;

                this.intents = results.intents;
                this.updateIntentChart(results.intents);

                this.problematicQueries = results.queries;
                this.loading = false;
            },
            error: (err) => {
                console.error('Failed to load metrics', err);
                this.loading = false; // Ensure loading stops even on error
            }
        });
    }

    updatePerformanceChart(data: PerformanceMetrics) {
        // Mocking trend data since API returns single point (Aggregation)
        // In real scenario, we'd need historical data API
        this.lineChartData = {
            labels: ['P95 Latency', 'Avg Latency'],
            datasets: [
                {
                    data: [data.p95LatencyMs, data.averageLatencyMs],
                    label: 'Latency (ms)',
                    fill: true,
                    tension: 0.5,
                    borderColor: 'rgba(75,192,192,1)',
                    backgroundColor: 'rgba(75,192,192,0.2)'
                }
            ]
        };
    }

    updateIntentChart(data: IntentDistribution[]) {
        this.pieChartData = {
            labels: data.map(d => d.intent),
            datasets: [{
                data: data.map(d => d.count),
                backgroundColor: ['#FF6384', '#36A2EB', '#FFCE56', '#4BC0C0', '#9966FF']
            }]
        };
    }
}
