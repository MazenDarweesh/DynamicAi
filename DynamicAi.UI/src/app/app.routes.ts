import { Routes } from '@angular/router';
import { ChatComponent } from './components/chat/chat.component';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { IntentReviewComponent } from './components/intent-review/intent-review.component';
import { TrainingDataComponent } from './components/training-data/training-data.component';
import { QueryInsightsPageComponent } from './components/query-insights-page/query-insights-page.component';
import { CostDashboardComponent } from './components/cost-dashboard/cost-dashboard.component';

export const routes: Routes = [
    { path: '', redirectTo: 'chat', pathMatch: 'full' },
    { path: 'chat', component: ChatComponent },
    { path: 'dashboard', component: DashboardComponent },
    { path: 'query-insights', component: QueryInsightsPageComponent },
    { path: 'intent-review', component: IntentReviewComponent },
    { path: 'training-data', component: TrainingDataComponent },
    { path: 'costs', component: CostDashboardComponent, runGuardsAndResolvers: 'always' }
];
