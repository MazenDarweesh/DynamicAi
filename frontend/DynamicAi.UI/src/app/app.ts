import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule, Router, NavigationEnd, ActivatedRoute } from '@angular/router';
import { filter, map, mergeMap } from 'rxjs/operators';
import { ChatSignalService } from './services/chat-signal.service';
import { PocSettingsService } from './services/poc-settings.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  templateUrl: './app.html',
  styleUrls: ['./app.css']
})
export class App {
  isSidebarOpen = window.innerWidth > 768;
  currentPageTitle = 'AI Assistant';
  showApiKey = false;
  private chatSignal = inject(ChatSignalService);
  private router = inject(Router);
  private activatedRoute = inject(ActivatedRoute);
  readonly pocSettings = inject(PocSettingsService);

  constructor() {
    // Subscribe to sidebar toggle signals from child components
    this.chatSignal.sidebarToggle$.subscribe(() => {
      this.toggleSidebar();
    });

    // Listen to route changes to update header title
    this.router.events.pipe(
      filter(event => event instanceof NavigationEnd),
      map(() => this.activatedRoute),
      map(route => {
        while (route.firstChild) route = route.firstChild;
        return route;
      }),
      filter(route => route.outlet === 'primary'),
      mergeMap(route => route.data)
    ).subscribe(data => {
      this.currentPageTitle = data['title'] || 'DynamicAI';
    });
  }

  toggleSidebar() {
    this.isSidebarOpen = !this.isSidebarOpen;
  }

  resetChat() {
    // If not on chat, navigate to chat
    if (this.router.url !== '/chat') {
      this.router.navigate(['/chat']);
    }
    // Trigger reset signal for ChatComponent
    this.chatSignal.triggerReset();
  }
}
