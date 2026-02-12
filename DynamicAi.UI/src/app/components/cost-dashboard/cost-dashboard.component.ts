import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { LlmCostService, BudgetInfo, DayCost, UsageLog, OperationCost } from '../../services/llm-cost.service';
import { BaseChartDirective } from 'ng2-charts';
import { ChartConfiguration, ChartOptions } from 'chart.js';

@Component({
    selector: 'app-cost-dashboard',
    standalone: true,
    imports: [CommonModule, BaseChartDirective],
    templateUrl: './cost-dashboard.component.html',
    styleUrls: ['./cost-dashboard.component.scss']
})
export class CostDashboardComponent implements OnInit {
    private costService = inject(LlmCostService);

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
                tension: 0.5,
                borderColor: 'black',
                backgroundColor: 'rgba(0,0,0,0.1)'
            }
        ]
    };
    public trendLineChartOptions: ChartOptions<'line'> = {
        responsive: true,
        plugins: {
            legend: { display: false },
            title: { display: true, text: '7-Day Cost Trend' }
        }
    };

    // Operation Mix Chart Data (Pie)
    public pieChartData: ChartConfiguration<'pie'>['data'] = {
        labels: [],
        datasets: [{
            data: []
        }]
    };
    public pieChartOptions: ChartOptions<'pie'> = {
        responsive: true,
        plugins: {
            legend: { position: 'top' },
            title: { display: true, text: 'Cost by Operation' }
        }
    };

    ngOnInit(): void {
        this.loadData();
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
}
