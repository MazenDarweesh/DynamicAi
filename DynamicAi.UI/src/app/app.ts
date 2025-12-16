import { Component, OnInit, ViewChild, ElementRef, AfterViewChecked, ChangeDetectorRef } from '@angular/core';
import { ApiService } from './services/api.service';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { environment } from '../environments/environment';
import { ChatMessage } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrls: ['./app.css']
})
export class App implements OnInit, AfterViewChecked {
  @ViewChild('scrollContainer') private scrollContainer!: ElementRef;

  status = 'Connected! ✅';
  apiUrl = environment.apiUrl;
  errorMsg = '';

  question = '';
  messages: ChatMessage[] = [];
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
    this.cdr.detectChanges();

    this.apiService.ask(currentQ).subscribe({
      next: (res) => {
        this.loading = false;
        console.log('API Response:', res);

        let data = res;
        let sql: string | undefined = undefined;
        let cacheId: string | undefined = undefined;

        // Handle QueryExecutionResult wrapper (backend refactor)
        // Supports camelCase (default) or PascalCase
        if (res) {
          if (res.data !== undefined) {
            data = res.data;
            sql = res.rewrittenSql;
            cacheId = res.cacheId;
          } else if (res.Data !== undefined) { // Fallback for PascalCase
            data = res.Data;
            sql = res.RewrittenSql;
            cacheId = res.CacheId;
          }
        }

        if (typeof data === 'string') {
          try {
            data = JSON.parse(data);
          } catch (e) {
            console.error('Failed to parse response as JSON', e);
          }
        }

        let newMessage: ChatMessage;

        if (data && data.Message) {
          newMessage = { isAi: true, text: data.Message, originalQuestion: currentQ, retryCount: 0 };
        } else if (Array.isArray(data)) {
          if (data.length > 0) {
            newMessage = { isAi: true, tableData: data, originalQuestion: currentQ, retryCount: 0 };
          } else {
            newMessage = { isAi: true, text: 'No results found.', originalQuestion: currentQ, retryCount: 0 };
          }
        } else {
          newMessage = { isAi: true, data: data, originalQuestion: currentQ, retryCount: 0 };
        }

        // Attach metadata
        newMessage.sql = sql;
        newMessage.cacheId = cacheId;
        newMessage.isLogged = false;

        this.messages.push(newMessage);
        this.cdr.detectChanges();
      },
      error: (err) => {
        this.loading = false;
        console.error('❌ [SendQuestion] Error:', err);

        let errorMsg = err.message;
        let sql = undefined;

        // Try to extract SQL from custom error object (from TestController)
        if (err.error && typeof err.error === 'object') {
          if (err.error.sql) sql = err.error.sql;
          if (err.error.error) errorMsg = err.error.error;
        }

        this.messages.push({
          isAi: true,
          text: 'Error: ' + errorMsg,
          originalQuestion: currentQ,
          retryCount: 0,
          sql: sql // Capture SQL for dislike functionality
        });
        this.cdr.detectChanges();
      }
    });
  }

  dislikeResponse(msg: ChatMessage) {
    console.log('🔴 [Dislike] Button clicked!');
    console.log('🔴 [Dislike] Message:', msg);

    if (!msg.originalQuestion) {
      console.warn('🔴 [Dislike] No original question found for this message');
      alert('لا يمكن الإبلاغ عن هذه الرسالة (لا يوجد سؤال أصلي)');
      return;
    }

    console.log('🔴 [Dislike] Original question:', msg.originalQuestion);
    console.log('🔴 [Dislike] Current retryCount:', msg.retryCount);

    this.loading = true;
    const retryCount = (msg.retryCount || 0) + 1;

    const request = {
      question: msg.originalQuestion,
      cacheId: msg.cacheId,
      sql: msg.sql,
      retryCount: retryCount,
      tenantId: 'default',
      companyId: 1,
      displayedMessage: msg.text // Send the text (answer or error) handling the dislike
    };

    console.log('🔴 [Dislike] Sending request:', request);
    console.log('🔴 [Dislike] API URL:', this.apiUrl + '/Feedback/dislike');

    this.apiService.dislike(request).subscribe({
      next: (res) => {
        console.log('✅ [Dislike] Response received:', res);
        this.loading = false;

        // Check for askForRetry (Smart Dislike Logic)
        if (res.askForRetry) {
          console.log('✅ [Dislike] Cache deleted, asking user for retry');
          msg.isLogged = true; // Still mark as logged/processed to disable dislike button
          msg.askRetry = true; // Show retry button in UI
          this.messages.push({
            isAi: true,
            text: res.message, // property in JSON response (was Message)
            isLogged: true,
            askRetry: true, // Also allow retrying from the response message
            originalQuestion: msg.originalQuestion
          });
        }
        else if (res.shouldStopRetrying) {
          console.log('✅ [Dislike] Stopping retries, logging issue');
          msg.isLogged = true;
          msg.retryCount = 2; // Max out
          this.messages.push({ isAi: true, text: res.message, isLogged: true });
        } else if (res.regeneratedData) {
          console.log('✅ [Dislike] Regenerated data:', res.regeneratedData);
          const index = this.messages.indexOf(msg);
          if (index > -1) {
            msg.retryCount = retryCount;

            const newData = res.regeneratedData;
            if (Array.isArray(newData)) {
              this.messages.push({
                isAi: true,
                tableData: newData,
                originalQuestion: msg.originalQuestion,
                retryCount: retryCount
              });
            } else if (newData.Message) {
              this.messages.push({
                isAi: true,
                text: newData.Message,
                originalQuestion: msg.originalQuestion,
                retryCount: retryCount
              });
            } else {
              this.messages.push({
                isAi: true,
                data: newData,
                originalQuestion: msg.originalQuestion,
                retryCount: retryCount
              });
            }
          }
        }
        this.cdr.detectChanges();
      },
      error: (err) => {
        console.error('❌ [Dislike] Error:', err);
        console.error('❌ [Dislike] Error status:', err.status);
        console.error('❌ [Dislike] Error message:', err.message);
        this.loading = false;
        msg.isLogged = true;
        this.messages.push({ isAi: true, text: 'تم تسجيل المشكلة للمراجعة ✅ (Error: ' + err.status + ')', isLogged: true });
        this.cdr.detectChanges();
      }
    });
  }
}
