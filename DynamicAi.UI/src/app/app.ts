import { Component, OnInit, ViewChild, ElementRef, AfterViewChecked, ChangeDetectorRef } from '@angular/core';
import { ApiService } from './services/api.service';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { environment } from '../environments/environment';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="app-container">
      <div class="glass-layout">
        <!-- Sidebar -->
        <div class="sidebar">
          <div class="sidebar-header">
            <img src="squareLogo.jpeg" alt="DynamicAi Logo" class="brand-logo">
            <div class="brand-name">DynamicAI</div>
          </div>
          
          <div class="new-chat-btn" (click)="resetChat()">
            <span>+</span> New Chat
          </div>

          <div class="history-list">
            <div class="history-label">Today</div>
            <div class="history-item active">Current Session</div>
          </div>

          <div class="user-profile">
            <div class="profile-icon">👤</div>
            <div class="profile-info">
              <span class="name">User</span>
              <span class="status">Pro Plan</span>
            </div>
          </div>
        </div>

        <!-- Main Chat Area -->
        <div class="main-content">
          <!-- Header / Status -->
          <header>
            <div class="status-badge" [ngClass]="{'connected': status === 'Connected! ✅', 'error': status.includes('Failed')}">
              <span class="dot"></span> {{ status }}
            </div>
            <div *ngIf="status.includes('Failed')" class="quick-actions">
              <button (click)="checkConnection()">Retry</button>
            </div>
          </header>

          <!-- Welcome Screen -->
          <div *ngIf="messages.length === 0" class="welcome-screen">
            <div class="hero-text">
              <h1>How can I help you today?</h1>
            </div>
            <div class="suggestions">
              <div class="suggestion-card" (click)="setQuestion('Show me sales for last month')">
                <span class="icon">📊</span>
                <span class="text">Show me sales for last month</span>
              </div>
              <div class="suggestion-card" (click)="setQuestion('List all active users')">
                <span class="icon">👥</span>
                <span class="text">List all active users</span>
              </div>
              <div class="suggestion-card" (click)="setQuestion('Analyze the latest trends')">
                <span class="icon">📈</span>
                <span class="text">Analyze the latest trends</span>
              </div>
            </div>
          </div>

          <!-- Chat History -->
          <div class="chat-scroll-area" #scrollContainer>
            <div *ngFor="let msg of messages" class="message-row" [ngClass]="{'ai': msg.isAi, 'user': !msg.isAi}">
              <div class="message-content">
                <div class="avatar">
                  {{ msg.isAi ? '🤖' : '👤' }}
                </div>
                <div class="bubble">
                  <div *ngIf="msg.text" class="text-content">{{ msg.text }}</div>
                  <div *ngIf="msg.data" class="json-content">
                    <div class="json-label">Data Response:</div>
                    <pre>{{ msg.data | json }}</pre>
                  </div>

                  <!-- Table Response -->
                  <div *ngIf="msg.tableData" class="table-response">
                    <div class="table-header">
                      <span class="icon">📊</span> Data Result
                      <span class="count-badge">{{ msg.tableData.length }} rows</span>
                    </div>
                    <div class="table-scroll">
                      <table>
                        <thead>
                          <tr>
                            <th *ngFor="let key of getKeys(msg.tableData[0])">{{ key }}</th>
                          </tr>
                        </thead>
                        <tbody>
                          <tr *ngFor="let row of msg.tableData">
                            <td *ngFor="let key of getKeys(msg.tableData[0])">{{ row[key] }}</td>
                          </tr>
                        </tbody>
                      </table>
                    </div>
                  </div>
                </div>
              </div>
            </div>
            
            <!-- Loading Indicator -->
            <div *ngIf="loading" class="message-row ai">
              <div class="message-content">
                <div class="avatar">🤖</div>
                <div class="bubble loading">
                  <span class="typing-dot"></span>
                  <span class="typing-dot"></span>
                  <span class="typing-dot"></span>
                </div>
              </div>
            </div>
          </div>

          <!-- Input Area -->
          <div class="input-area">
            <div class="input-wrapper">
              <textarea 
                [(ngModel)]="question" 
                (keydown.enter)="onEnter($event)" 
                placeholder="Message DynamicAi..." 
                rows="1"
                [disabled]="loading"
              ></textarea>
              <button (click)="sendQuestion()" [disabled]="loading || !question.trim()" class="send-btn">
                ➤
              </button>
            </div>
            <div class="footer-text">
              DynamicAI can make mistakes. Consider checking important information.
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host {
      --primary: #6366f1;
      --primary-glow: rgba(99, 102, 241, 0.4);
      --bg-dark: #0f172a;
      --glass-bg: rgba(15, 23, 42, 0.6);
      --glass-border: rgba(255, 255, 255, 0.08);
      --sidebar-bg: rgba(0, 0, 0, 0.3);
      --text-main: #f8fafc;
      --text-muted: #94a3b8;
    }

    .app-container {
      height: 100vh;
      background: linear-gradient(135deg, #0f172a 0%, #1e1b4b 100%);
      font-family: 'Inter', system-ui, sans-serif;
      color: var(--text-main);
      display: flex;
      justify-content: center;
      align-items: center;
    }

    .glass-layout {
      width: 100%;
      height: 100%;
      display: flex;
      background: var(--glass-bg);
      backdrop-filter: blur(20px);
    }

    /* Sidebar */
    .sidebar {
      width: 280px;
      background: var(--sidebar-bg);
      border-right: 1px solid var(--glass-border);
      display: flex;
      flex-direction: column;
      padding: 20px;
    }

    .sidebar-header {
      margin-bottom: 20px;
      display: flex;
      align-items: center;
      gap: 12px;
      padding-left: 5px;
    }

    .brand-logo {
      width: 32px;
      height: 32px;
      object-fit: contain;
      border-radius: 6px;
    }

    .brand-name {
      font-size: 1.5rem;
      font-weight: 700;
      background: linear-gradient(to right, #fff, #cbd5e1);
      -webkit-background-clip: text;
      -webkit-text-fill-color: transparent;
      letter-spacing: -0.5px;
      line-height: 1;
      padding-top: 2px; /* Visual alignment correction */
    }

    .new-chat-btn {
      background: rgba(255, 255, 255, 0.05);
      border: 1px solid var(--glass-border);
      padding: 12px;
      border-radius: 12px;
      cursor: pointer;
      display: flex;
      align-items: center;
      gap: 10px;
      font-weight: 500;
      transition: all 0.2s;
    }
    .new-chat-btn:hover { background: rgba(255, 255, 255, 0.1); border-color: var(--primary); }

    .history-list { flex: 1; margin-top: 30px; }
    .history-label { font-size: 0.75rem; color: var(--text-muted); margin-bottom: 10px; text-transform: uppercase; letter-spacing: 1px; }
    .history-item {
      padding: 10px 12px;
      border-radius: 8px;
      cursor: pointer;
      color: var(--text-muted);
      font-size: 0.9rem;
      transition: background 0.2s;
    }
    .history-item:hover, .history-item.active { background: rgba(255, 255, 255, 0.05); color: white; }

    .user-profile {
      border-top: 1px solid var(--glass-border);
      padding-top: 20px;
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .profile-icon {
      width: 36px; height: 36px;
      background: linear-gradient(135deg, #6366f1, #a855f7);
      border-radius: 50%;
      display: flex; align-items: center; justify-content: center;
    }
    .profile-info { display: flex; flex-direction: column; }
    .profile-info .name { font-weight: 600; font-size: 0.9rem; }
    .profile-info .status { font-size: 0.75rem; color: var(--text-muted); }

    /* Main Content */
    .main-content {
      flex: 1;
      display: flex;
      flex-direction: column;
      position: relative;
    }

    header {
      padding: 15px 30px;
      display: flex;
      justify-content: space-between;
      align-items: center;
      position: absolute;
      top: 0; left: 0; right: 0;
      z-index: 10;
    }

    .status-badge {
      font-size: 0.8rem;
      padding: 4px 10px;
      border-radius: 20px;
      background: rgba(0, 0, 0, 0.2);
      border: 1px solid var(--glass-border);
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .status-badge.connected { color: #4ade80; border-color: rgba(74, 222, 128, 0.2); }
    .status-badge.error { color: #f87171; border-color: rgba(248, 113, 113, 0.2); }
    .dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
    .quick-actions button { background: none; border: 1px solid var(--glass-border); color: white; padding: 4px 8px; border-radius: 4px; cursor: pointer; font-size: 0.8rem; }

    /* Welcome Screen */
    .welcome-screen {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 20px;
    }
    .hero-text h1 { font-size: 2rem; font-weight: 600; margin-bottom: 40px; text-align: center; }
    
    .suggestions { display: grid; grid-template-columns: repeat(2, 1fr); gap: 15px; max-width: 700px; width: 100%; }
    .suggestion-card {
      background: rgba(255, 255, 255, 0.03);
      border: 1px solid var(--glass-border);
      padding: 20px;
      border-radius: 16px;
      cursor: pointer;
      transition: all 0.2s;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .suggestion-card:hover { background: rgba(255, 255, 255, 0.08); transform: translateY(-2px); border-color: var(--primary); }
    .suggestion-card .icon { font-size: 1.5rem; }
    .suggestion-card .text { font-size: 0.95rem; color: var(--text-muted); }

    /* Chat Area */
    .chat-scroll-area {
      flex: 1;
      overflow-y: auto;
      padding: 80px 0 150px;
    }

    .message-row {
      padding: 30px 0;
      border-bottom: 1px solid rgba(255, 255, 255, 0.02);
    }
    .message-row.ai { background: rgba(0, 0, 0, 0.1); }

    .message-content {
      max-width: 800px;
      margin: 0 auto;
      padding: 0 20px;
      display: flex;
      gap: 20px;
    }

    .avatar {
      width: 32px; height: 32px;
      border-radius: 6px;
      display: flex; align-items: center; justify-content: center;
      font-size: 1.2rem;
      flex-shrink: 0;
    }
    .user .avatar { background: var(--primary); }
    .ai .avatar { background: #10a37f; }

    .bubble { flex: 1; line-height: 1.6; font-size: 1rem; padding-top: 2px; }
    .json-content { background: #1e1e1e; border-radius: 8px; overflow: hidden; margin-top: 10px; border: 1px solid var(--glass-border); }
    .json-label { padding: 8px 12px; background: rgba(255,255,255,0.05); font-size: 0.75rem; color: var(--text-muted); border-bottom: 1px solid var(--glass-border); }
    pre { margin: 0; padding: 15px; font-family: 'Fira Code', monospace; font-size: 0.85rem; color: #d4d4d4; overflow-x: auto; }

    /* Input Area */
    .input-area {
      position: absolute;
      bottom: 0; left: 0; right: 0;
      padding: 20px;
      background: linear-gradient(to top, var(--bg-dark), transparent);
      display: flex;
      flex-direction: column;
      align-items: center;
    }

    .input-wrapper {
      width: 100%;
      max-width: 800px;
      position: relative;
      background: rgba(30, 41, 59, 0.8);
      backdrop-filter: blur(10px);
      border: 1px solid var(--glass-border);
      border-radius: 16px;
      box-shadow: 0 4px 20px rgba(0, 0, 0, 0.2);
      display: flex;
      align-items: center;
      padding: 5px;
    }
    .input-wrapper:focus-within { border-color: var(--primary); box-shadow: 0 0 0 2px var(--primary-glow); }

    textarea {
      flex: 1;
      background: transparent;
      border: none;
      color: white;
      font-family: inherit;
      font-size: 1rem;
      padding: 15px 20px;
      resize: none;
      max-height: 200px;
      outline: none;
    }

    .send-btn {
      background: var(--primary);
      color: white;
      border: none;
      width: 40px; height: 40px;
      border-radius: 10px;
      cursor: pointer;
      display: flex; align-items: center; justify-content: center;
      transition: all 0.2s;
      margin-right: 5px;
    }
    .send-btn:hover:not(:disabled) { background: #4f46e5; transform: scale(1.05); }
    .send-btn:disabled { opacity: 0.5; cursor: not-allowed; background: #475569; }

    .footer-text { margin-top: 12px; font-size: 0.75rem; color: var(--text-muted); }

    .typing-dot {
      display: inline-block; width: 6px; height: 6px; background: #ccc; border-radius: 50%; margin: 0 2px;
      animation: typing 1.4s infinite ease-in-out;
    }
    .typing-dot:nth-child(1) { animation-delay: 0s; }
    .typing-dot:nth-child(2) { animation-delay: 0.2s; }
    .typing-dot:nth-child(3) { animation-delay: 0.4s; }
    @keyframes typing { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(-5px); } }

    /* Table Styles */
    .table-response {
      margin-top: 15px;
      background: rgba(30, 41, 59, 0.5);
      border: 1px solid var(--glass-border);
      border-radius: 12px;
      overflow: hidden;
      width: 100%;
    }
    
    .table-header {
      padding: 10px 15px;
      background: rgba(0, 0, 0, 0.2);
      border-bottom: 1px solid var(--glass-border);
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.85rem;
      color: var(--text-muted);
      font-weight: 500;
    }
    
    .count-badge {
      background: rgba(255, 255, 255, 0.1);
      padding: 2px 8px;
      border-radius: 10px;
      font-size: 0.75rem;
      color: white;
    }

    .table-scroll {
      overflow-x: auto;
      max-height: 400px;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.9rem;
      white-space: nowrap;
    }

    th {
      background: rgba(255, 255, 255, 0.05);
      padding: 12px 15px;
      text-align: left;
      font-weight: 600;
      color: var(--text-muted);
      position: sticky;
      top: 0;
      backdrop-filter: blur(5px);
      border-bottom: 1px solid var(--glass-border);
    }

    td {
      padding: 12px 15px;
      border-bottom: 1px solid var(--glass-border);
      color: var(--text-main);
    }

    tr:last-child td { border-bottom: none; }
    tr:hover td { background: rgba(255, 255, 255, 0.02); }
  `]
})
export class App implements OnInit, AfterViewChecked {
  @ViewChild('scrollContainer') private scrollContainer!: ElementRef;

  status = 'Connected! ✅';
  apiUrl = environment.apiUrl;
  errorMsg = '';

  question = '';
  messages: { isAi: boolean, text?: string, data?: any, tableData?: any[] }[] = [];
  loading = false;

  constructor(private apiService: ApiService, private cdr: ChangeDetectorRef) { }

  ngOnInit() {
    // Initial check disabled to save tokens
    // this.checkConnection();
  }

  ngAfterViewChecked() {
    this.scrollToBottom();
  }

  scrollToBottom(): void {
    try {
      this.scrollContainer.nativeElement.scrollTop = this.scrollContainer.nativeElement.scrollHeight;
    } catch (err) { }
  }

  checkConnection() {
    this.status = 'Checking AI System...';
    this.errorMsg = '';

    const timeoutId = setTimeout(() => {
      if (this.status === 'Checking AI System...') {
        this.status = 'Connection Failed ❌';
        this.errorMsg = 'Timeout: Backend did not respond in 15s. (AI Cold Start?)';
        this.cdr.detectChanges();
      }
    }, 15000);

    this.apiService.ask('Hello').subscribe({
      next: (res) => {
        clearTimeout(timeoutId);
        this.status = 'Connected! ✅';
        this.cdr.detectChanges();
      },
      error: (err) => {
        clearTimeout(timeoutId);
        this.status = 'Connection Failed ❌';
        this.errorMsg = err.message;
        if (err.status === 0) this.errorMsg = 'Network Error (SSL/CORS)';
        this.cdr.detectChanges();
      }
    });
  }

  onEnter(event: any) {
    if (!event.shiftKey) {
      event.preventDefault();
      this.sendQuestion();
    }
  }

  setQuestion(q: string) {
    this.question = q;
    this.sendQuestion();
  }

  resetChat() {
    this.messages = [];
  }

  getKeys(obj: any): string[] {
    return obj ? Object.keys(obj) : [];
  }

  sendQuestion() {
    if (!this.question.trim()) return;

    const currentQ = this.question;
    this.messages.push({ isAi: false, text: currentQ });
    this.question = '';
    this.loading = true;
    this.cdr.detectChanges(); // Force update for user message

    this.apiService.ask(currentQ).subscribe({
      next: (res) => {
        this.loading = false;
        console.log('API Response:', res);

        let data = res;
        // Try to parse if string
        if (typeof res === 'string') {
          try {
            data = JSON.parse(res);
          } catch (e) {
            console.error('Failed to parse response as JSON', e);
          }
        }

        // Check for Message property (Text response)
        if (data && data.Message) {
          this.messages.push({ isAi: true, text: data.Message });
        }
        // Check for Array (Table response)
        else if (Array.isArray(data)) {
          if (data.length > 0) {
            this.messages.push({ isAi: true, tableData: data });
          } else {
            this.messages.push({ isAi: true, text: 'No results found.' });
          }
        }
        // Fallback (JSON response)
        else {
          this.messages.push({ isAi: true, data: data });
        }
        this.cdr.detectChanges(); // Force update for AI response
      },
      error: (err) => {
        this.loading = false;
        this.messages.push({ isAi: true, text: 'Error: ' + err.message });
        this.cdr.detectChanges();
      }
    });
  }
}
