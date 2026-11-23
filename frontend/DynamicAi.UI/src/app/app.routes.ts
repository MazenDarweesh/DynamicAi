import { Routes } from '@angular/router';
import { ChatComponent } from './components/chat/chat.component';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { IntentReviewComponent } from './components/intent-review/intent-review.component';
import { TrainingDataComponent } from './components/training-data/training-data.component';
import { QueryInsightsPageComponent } from './components/query-insights-page/query-insights-page.component';
import { CostDashboardComponent } from './components/cost-dashboard/cost-dashboard.component';

export const routes: Routes = [
    { path: '', redirectTo: 'chat', pathMatch: 'full' },
    { path: 'chat', component: ChatComponent, data: { title: 'AI Assistant' } },
    { path: 'dashboard', component: DashboardComponent, data: { title: 'Smart Analytics' } },
    { path: 'query-insights', component: QueryInsightsPageComponent, data: { title: 'Query Insights' } },
    { path: 'intent-review', component: IntentReviewComponent, data: { title: 'Intent Management' } },
    { path: 'training-data', component: TrainingDataComponent, data: { title: 'Knowledge Base' } },
    { path: 'costs', component: CostDashboardComponent, data: { title: 'Financial Overview' }, runGuardsAndResolvers: 'always' }
];
