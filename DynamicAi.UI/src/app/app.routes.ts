import { Routes } from '@angular/router';
import { ChatComponent } from './components/chat/chat.component';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { IntentReviewComponent } from './components/intent-review/intent-review.component';
import { TrainingDataComponent } from './components/training-data/training-data.component';

export const routes: Routes = [
    { path: '', redirectTo: 'chat', pathMatch: 'full' },
    { path: 'chat', component: ChatComponent },
    { path: 'dashboard', component: DashboardComponent },
    { path: 'intent-review', component: IntentReviewComponent },
    { path: 'training-data', component: TrainingDataComponent }
];
