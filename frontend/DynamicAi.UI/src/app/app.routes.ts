import { Routes } from '@angular/router';

export const routes: Routes = [
    { path: '', redirectTo: 'chat', pathMatch: 'full' },
    {
        path: 'chat',
        loadComponent: () => import('./components/chat/chat.component').then(m => m.ChatComponent),
        data: { title: 'AI Assistant' }
    },
    {
        path: 'dashboard',
        loadComponent: () => import('./components/dashboard/dashboard.component').then(m => m.DashboardComponent),
        data: { title: 'Smart Analytics' }
    },
    {
        path: 'query-insights',
        loadComponent: () => import('./components/query-insights-page/query-insights-page.component').then(m => m.QueryInsightsPageComponent),
        data: { title: 'Query Insights' }
    },
    {
        path: 'training-data',
        loadComponent: () => import('./components/training-data/training-data.component').then(m => m.TrainingDataComponent),
        data: { title: 'Knowledge Base' }
    },
    {
        path: 'costs',
        loadComponent: () => import('./components/cost-dashboard/cost-dashboard.component').then(m => m.CostDashboardComponent),
        data: { title: 'Financial Overview' },
        runGuardsAndResolvers: 'always'
    }
];
