import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { ChatSignalService } from './services/chat-signal.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, RouterModule],
  templateUrl: './app.html',
  styleUrls: ['./app.css']
})
export class App {
  isSidebarOpen = true;
  private chatSignal = inject(ChatSignalService);
  private router = inject(Router);

  constructor() {
    // Subscribe to sidebar toggle signals from child components
    this.chatSignal.sidebarToggle$.subscribe(() => {
      this.toggleSidebar();
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
