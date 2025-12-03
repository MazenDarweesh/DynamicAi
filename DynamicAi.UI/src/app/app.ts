import { Component, OnInit } from '@angular/core';
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
      <div class="glass-panel">
        <header>
          <h1>✨ DynamicAi</h1>
          <div class="status-badge" [ngClass]="{'connected': status === 'Connected! ✅', 'error': status.includes('Failed')}">
            <span class="dot"></span> {{ status }}
          </div>
        </header>

        <!-- Connection Error State -->
        <div *ngIf="status.includes('Failed')" class="error-box">
          <h3>⚠️ Connection Issue</h3>
          <p>{{ errorMsg }}</p>
          <div class="actions">
            <a [href]="apiUrl + '/Test/ping'" target="_blank" class="btn secondary">Trust SSL Certificate</a>
            <button (click)="checkConnection()" class="btn primary">Retry Connection</button>
          </div>
        </div>

        <!-- Chat Interface -->
        <div *ngIf="status === 'Connected! ✅'" class="chat-interface">
          
          <!-- Messages Area -->
          <div class="messages-area">
            <div *ngIf="!response && !loading" class="welcome-message">
              <h2>Welcome back! 👋</h2>
              <p>Ask me anything about your data.</p>
            </div>

            <!-- User Question -->
            <div *ngIf="lastQuestion" class="message user-message">
              <div class="avatar">👤</div>
              <div class="bubble">{{ lastQuestion }}</div>
            </div>

            <!-- AI Response -->
            <div *ngIf="loading" class="message ai-message">
              <div class="avatar">🤖</div>
              <div class="bubble loading">
                <span class="typing-dot"></span>
                <span class="typing-dot"></span>
                <span class="typing-dot"></span>
              </div>
            </div>

            <div *ngIf="response" class="message ai-message fade-in">
              <div class="avatar">🤖</div>
              <div class="bubble response-content">
                <!-- Simple Message -->
                <div *ngIf="response.Message" class="text-response">
                  {{ response.Message }}
                </div>
                
                <!-- Full JSON Log (Requested) -->
                <div *ngIf="!response.Message" class="json-log">
                  <div class="log-header">📊 Data Response</div>
                  <pre>{{ response | json }}</pre>
                </div>
              </div>
            </div>
          </div>

          <!-- Input Area -->
          <div class="input-area">
            <input 
              [(ngModel)]="question" 
              (keyup.enter)="sendQuestion()" 
              placeholder="Type your question here..." 
              [disabled]="loading"
            >
            <button (click)="sendQuestion()" [disabled]="loading || !question.trim()" class="send-btn">
              ➤
            </button>
          </div>

        </div>
      </div>
    </div>
  `,
  styles: [`
    :host {
      --primary: #6366f1;
      --bg-dark: #0f172a;
      --glass-bg: rgba(30, 41, 59, 0.7);
      --glass-border: rgba(255, 255, 255, 0.1);
      --text-main: #f8fafc;
      --text-muted: #94a3b8;
    }

    .app-container {
      min-height: 100vh;
      background: linear-gradient(135deg, #0f172a 0%, #1e1b4b 100%);
      display: flex;
      align-items: center;
      justify-content: center;
      font-family: 'Inter', system-ui, sans-serif;
      color: var(--text-main);
      padding: 20px;
    }

    .glass-panel {
      width: 100%;
      max-width: 900px;
      height: 85vh;
      background: var(--glass-bg);
      backdrop-filter: blur(12px);
      border: 1px solid var(--glass-border);
      border-radius: 24px;
      box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }

    header {
      padding: 20px 30px;
      border-bottom: 1px solid var(--glass-border);
      display: flex;
      justify-content: space-between;
      align-items: center;
    }

    h1 { margin: 0; font-size: 1.5rem; background: linear-gradient(to right, #818cf8, #c084fc); -webkit-background-clip: text; -webkit-text-fill-color: transparent; }

    .status-badge {
      font-size: 0.85rem;
      padding: 6px 12px;
      border-radius: 20px;
      background: rgba(255, 255, 255, 0.05);
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .status-badge.connected { color: #4ade80; background: rgba(74, 222, 128, 0.1); }
    .status-badge.error { color: #f87171; background: rgba(248, 113, 113, 0.1); }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: currentColor; }

    .chat-interface {
      flex: 1;
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }

    .messages-area {
      flex: 1;
      padding: 30px;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 20px;
    }

    .welcome-message {
      text-align: center;
      margin-top: 10vh;
      color: var(--text-muted);
    }

    .message {
      display: flex;
      gap: 15px;
      max-width: 85%;
      animation: slideIn 0.3s ease-out;
    }
    .user-message { align-self: flex-end; flex-direction: row-reverse; }
    .ai-message { align-self: flex-start; }

    .avatar {
      width: 40px;
      height: 40px;
      border-radius: 50%;
      background: rgba(255, 255, 255, 0.1);
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 1.2rem;
    }

    .bubble {
      padding: 15px 20px;
      border-radius: 18px;
      background: rgba(255, 255, 255, 0.05);
      line-height: 1.5;
    }
    .user-message .bubble {
      background: var(--primary);
      color: white;
      border-bottom-right-radius: 4px;
    }
    .ai-message .bubble {
      border-top-left-radius: 4px;
      border: 1px solid var(--glass-border);
    }

    .json-log {
      margin-top: 10px;
      background: #1e1e1e;
      border-radius: 8px;
      overflow: hidden;
    }
    .log-header {
      padding: 8px 12px;
      background: #2d2d2d;
      font-size: 0.8rem;
      color: #aaa;
      border-bottom: 1px solid #333;
    }
    pre {
      margin: 0;
      padding: 15px;
      font-family: 'Fira Code', monospace;
      font-size: 0.85rem;
      color: #d4d4d4;
      overflow-x: auto;
    }

    .input-area {
      padding: 20px 30px;
      background: rgba(0, 0, 0, 0.2);
      display: flex;
      gap: 15px;
    }

    input {
      flex: 1;
      background: rgba(255, 255, 255, 0.05);
      border: 1px solid var(--glass-border);
      padding: 15px 20px;
      border-radius: 12px;
      color: white;
      font-size: 1rem;
      outline: none;
      transition: all 0.2s;
    }
    input:focus {
      background: rgba(255, 255, 255, 0.1);
      border-color: var(--primary);
    }

    .send-btn {
      background: var(--primary);
      color: white;
      border: none;
      width: 50px;
      border-radius: 12px;
      cursor: pointer;
      font-size: 1.2rem;
      transition: transform 0.1s;
    }
    .send-btn:active { transform: scale(0.95); }
    .send-btn:disabled { opacity: 0.5; cursor: not-allowed; }

    .error-box {
      margin: 20px;
      padding: 20px;
      background: rgba(239, 68, 68, 0.1);
      border: 1px solid rgba(239, 68, 68, 0.2);
      border-radius: 12px;
      text-align: center;
    }
    .btn {
      padding: 8px 16px;
      border-radius: 8px;
      border: none;
      cursor: pointer;
      font-weight: 600;
      text-decoration: none;
      display: inline-block;
      margin: 0 5px;
    }
    .btn.primary { background: var(--primary); color: white; }
    .btn.secondary { background: rgba(255, 255, 255, 0.1); color: white; }

    @keyframes slideIn {
      from { opacity: 0; transform: translateY(10px); }
      to { opacity: 1; transform: translateY(0); }
    }
    
    .typing-dot {
      display: inline-block;
      width: 6px;
      height: 6px;
      background: #ccc;
      border-radius: 50%;
      margin: 0 2px;
      animation: typing 1.4s infinite ease-in-out;
    }
    .typing-dot:nth-child(1) { animation-delay: 0s; }
    .typing-dot:nth-child(2) { animation-delay: 0.2s; }
    .typing-dot:nth-child(3) { animation-delay: 0.4s; }
    
    @keyframes typing {
      0%, 100% { transform: translateY(0); }
      50% { transform: translateY(-5px); }
    }
  `]
})
export class App implements OnInit {
  status = 'Checking...';
  apiUrl = environment.apiUrl;
  errorMsg = '';

  question = '';
  lastQuestion = '';
  response: any = null;
  loading = false;

  constructor(private apiService: ApiService) { }

  ngOnInit() {
    this.checkConnection();
  }

  checkConnection() {
    this.status = 'Checking AI System...';
    this.errorMsg = '';

    const timeoutId = setTimeout(() => {
      if (this.status === 'Checking AI System...') {
        this.status = 'Connection Failed ❌';
        this.errorMsg = 'Timeout: Backend did not respond in 15s. (AI Cold Start?)';
      }
    }, 15000);

    // User requested to NOT use ping, but use 'ask' endpoint.
    // We send a lightweight 'Hello' to check if the pipeline is up.
    this.apiService.ask('Hello').subscribe({
      next: (res) => {
        clearTimeout(timeoutId);
        this.status = 'Connected! ✅';
      },
      error: (err) => {
        clearTimeout(timeoutId);
        this.status = 'Connection Failed ❌';
        this.errorMsg = err.message;
        if (err.status === 0) this.errorMsg = 'Network Error (SSL/CORS)';
      }
    });
  }

  sendQuestion() {
    if (!this.question.trim()) return;

    this.lastQuestion = this.question;
    this.loading = true;
    this.response = null;
    const currentQ = this.question;
    this.question = ''; // Clear input immediately

    this.apiService.ask(currentQ).subscribe({
      next: (res) => {
        this.loading = false;
        this.response = res;
      },
      error: (err) => {
        this.loading = false;
        this.response = { Message: 'Error: ' + err.message };
        console.error(err);
      }
    });
  }
}
