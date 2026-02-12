import { Component, OnInit, inject, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { LlmCostService, BudgetInfo, DayCost, UsageLog, OperationCost } from '../../services/llm-cost.service';
import { BaseChartDirective } from 'ng2-charts';
import { ChartConfiguration, ChartOptions } from 'chart.js';
import { Router, NavigationEnd, Event } from '@angular/router';
import { filter, Subscription } from 'rxjs';

@Component({
    selector: 'app-cost-dashboard',
    standalone: true,
    imports: [CommonModule, BaseChartDirective],
    templateUrl: './cost-dashboard.component.html',
    styleUrls: ['./cost-dashboard.component.scss']
})
export class CostDashboardComponent implements OnInit, OnDestroy {
    private costService = inject(LlmCostService);
    private router = inject(Router);
    private routerSub?: Subscription;

    budgetInfo?: BudgetInfo;
    dailyTotal?: DayCost;
    operationCosts: OperationCost[] = [];
    recentLogs: UsageLog[] = [];

    // Trend Chart Data
    public trendLineChartData: ChartConfiguration<'line'>['data'] = {
        labels: [],
        datasets: [
            {
                data: [],
                label: 'Daily Cost ($)',
                fill: true,
                tension: 0.4,
                borderColor: '#4facfe',
                backgroundColor: 'rgba(79, 172, 254, 0.1)',
                pointBackgroundColor: '#fff',
                pointBorderColor: '#4facfe',
                pointHoverBackgroundColor: '#4facfe',
                pointHoverBorderColor: '#fff'
            }
        ]
    };
    public trendLineChartOptions: ChartOptions<'line'> = {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
            legend: { display: false },
            title: { display: false } // Hidden title for cleaner look
        },
        scales: {
            x: { grid: { display: false }, ticks: { color: '#888' } },
            y: { grid: { color: 'rgba(255,255,255,0.05)' }, ticks: { color: '#888' } }
        }
    };

    // Operation Mix Chart Data (Pie)
    public pieChartData: ChartConfiguration<'pie'>['data'] = {
        labels: [],
        datasets: [{
            data: [],
            backgroundColor: ['#4facfe', '#00f2fe', '#43e97b', '#fa709a', '#fee140'],
            borderColor: 'rgba(30, 30, 40, 0.7)',
            borderWidth: 2
        }]
    };
    public pieChartOptions: ChartOptions<'pie'> = {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
            legend: { position: 'right', labels: { color: '#ccc', usePointStyle: true } },
            title: { display: false }
        }
    };

    ngOnInit(): void {
        this.loadData();

        // Subscribe to router events for refresh on re-navigation
        this.routerSub = this.router.events.pipe(
            filter((event: Event): event is NavigationEnd => event instanceof NavigationEnd)
        ).subscribe(() => {
            this.loadData();
        });
    }

    ngOnDestroy(): void {
        this.routerSub?.unsubscribe();
    }

    loadData(): void {
        // 1. Budget Info
        this.costService.getBudgetStatus().subscribe(info => this.budgetInfo = info);

        // 2. Daily Total
        this.costService.getDailyTotal().subscribe(total => this.dailyTotal = total);

        // 3. Trend
        this.costService.getTrend(7).subscribe(trend => {
            this.trendLineChartData.labels = trend.map(t => new Date(t.date).toLocaleDateString());
            this.trendLineChartData.datasets[0].data = trend.map(t => t.costUsd);
            // Trigger chart update if needed (BaseChartDirective handles it usually)
        });

        // 4. By Operation
        this.costService.getCostByOperation().subscribe(ops => {
            const labels = Object.keys(ops);
            const data = Object.values(ops).map(o => o.costUsd);

            this.pieChartData.labels = labels;
            this.pieChartData.datasets[0].data = data;
        });

        // 5. Recent Logs
        this.costService.getRecentLogs(20).subscribe(logs => this.recentLogs = logs);
    }

    getStatusColor(status: string): string {
        switch (status) {
            case 'Allowed': return 'bg-success';
            case 'Warning': return 'bg-warning text-dark';
            case 'Exceeded': return 'bg-danger';
            default: return 'bg-secondary';
        }
    }

    formatModelName(name: string): string {
        return name ? name.replace('models/', '') : 'Unknown';
    }
}
