import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BaseChartDirective } from 'ng2-charts';
import { MetricsService, PerformanceMetrics, IntentDistribution, CostBreakdown, UserExperienceMetrics, ProblematicQuery } from '../../services/metrics.service';
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

        // ForkJoin would be better, but doing sequential for simplicity of debugging
        this.metricsService.getPerformanceMetrics().subscribe(data => {
            this.performance = data;
            this.updatePerformanceChart(data);
        });

        this.metricsService.getUserExperience().subscribe(data => this.experience = data);
        this.metricsService.getCostBreakdown().subscribe(data => this.costs = data);

        this.metricsService.getIntentDistribution().subscribe(data => {
            this.intents = data;
            this.updateIntentChart(data);
        });

        this.metricsService.getProblematicQueries().subscribe(data => {
            this.problematicQueries = data;
            this.loading = false;
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
