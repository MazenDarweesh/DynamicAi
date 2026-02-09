import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewChecked, ChangeDetectorRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../services/api.service';
import { ChatSignalService } from '../../services/chat-signal.service';
import { environment } from '../../../environments/environment';
import { ChatMessage } from '../../models';

@Component({
    selector: 'app-chat',
    standalone: true,
    imports: [CommonModule, FormsModule],
    templateUrl: './chat.component.html',
    styleUrls: ['./chat.component.css']
})
export class ChatComponent implements OnInit, OnDestroy, AfterViewChecked {
    @ViewChild('scrollContainer') private scrollContainer!: ElementRef;


    private apiService = inject(ApiService);
    private cdr = inject(ChangeDetectorRef);
    private chatSignal = inject(ChatSignalService);

    status = 'Connected! ✅';
    apiUrl = environment.apiUrl;
    errorMsg = '';

    question = '';
    messages: ChatMessage[] = [];
    loading = false;
    // sidebar state is now managed by parent (App), we emit event to toggle it

    // Context
    lastQuestion: string | undefined = undefined;
    lastSql: string | undefined = undefined;
    confirmedQuestion: string | undefined = undefined;
    confirmedSql: string | undefined = undefined;

    ngOnInit() {
        // Subscribe to external reset signals (e.g. from Sidebar)
        this.chatSignal.reset$.subscribe(() => {
            this.resetChat();
        });

        // Load Persistent State
        const savedState = this.chatSignal.getState();
        if (savedState.messages.length > 0) {
            this.messages = savedState.messages;
            this.lastQuestion = savedState.lastQuestion;
            this.lastSql = savedState.lastSql;
            this.confirmedQuestion = savedState.confirmedQuestion;
            this.confirmedSql = savedState.confirmedSql;
        }
    }

    ngOnDestroy() {
        // Save State when component is destroyed (e.g. navigating away)
        this.chatSignal.saveState(this.messages, this.lastQuestion, this.lastSql, this.confirmedQuestion, this.confirmedSql);
    }

    toggleSidebar() {
        // Use service to communicate with App shell (since @Output doesn't work through router-outlet)
        this.chatSignal.triggerSidebarToggle();
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

        const sessionId = this.chatSignal.getOrCreateSessionId();
        this.apiService.ask('Hello', undefined, undefined, sessionId).subscribe({
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

    setQuestion(q: string, retryCount: number = 0) {
        this.question = q;
        this.sendQuestion(retryCount);
    }

    resetChat() {
        this.messages = [];
        this.lastQuestion = undefined;
        this.lastSql = undefined;
        this.confirmedQuestion = undefined;
        this.confirmedSql = undefined;
        this.chatSignal.clearState(); // Clear persistence
        this.cdr.detectChanges();
    }

    getKeys(obj: any): string[] {
        return obj ? Object.keys(obj) : [];
    }

    sendQuestion(retryCount: number = 0) {
        console.log('🚀 [SendQuestion] Question:', this.question, 'RetryCount:', retryCount);
        if (!this.question.trim()) return;

        const currentQ = this.question;
        this.messages.push({ isAi: false, text: currentQ });
        this.question = '';
        this.loading = true;
        this.cdr.detectChanges();

        const sessionId = this.chatSignal.getOrCreateSessionId();
        this.apiService.ask(currentQ, this.lastQuestion, this.lastSql, sessionId).subscribe({
            next: (res) => {
                this.loading = false;
                console.log('API Response:', res);

                let data = res;
                let sql: string | undefined = undefined;
                let cacheId: string | undefined = undefined;

                if (res) {
                    if (res.data !== undefined) {
                        data = res.data;
                        sql = res.rewrittenSql;
                        cacheId = res.cacheId;
                    } else if (res.Data !== undefined) {
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

                // Helper to get case-insensitive property
                const getProp = (obj: any, key: string) => obj[key] || obj[key.toLowerCase()] || obj[key.charAt(0).toUpperCase() + key.slice(1)];

                const msgType = getProp(data, 'Type');
                const msgText = getProp(data, 'Message');

                if (msgText && !msgType) {
                    // Standard message
                    newMessage = { isAi: true, text: msgText, originalQuestion: currentQ, retryCount: retryCount };
                } else if (msgType === 'Question') {
                    // Clarification Question
                    newMessage = { isAi: true, text: msgText, originalQuestion: currentQ, retryCount: retryCount };
                } else if (Array.isArray(data)) {
                    if (data.length > 0) {
                        newMessage = { isAi: true, tableData: data, originalQuestion: currentQ, retryCount: retryCount };
                    } else {
                        newMessage = { isAi: true, text: 'No results found.', originalQuestion: currentQ, retryCount: retryCount };
                    }
                } else {
                    newMessage = { isAi: true, data: data, originalQuestion: currentQ, retryCount: retryCount };
                }

                newMessage.sql = sql;
                newMessage.cacheId = cacheId;
                newMessage.isLogged = false;
                newMessage.queryLogId = res.queryLogId || res.QueryLogId;
                newMessage.intent = res.intent ?? res.Intent;

                // Capture Query Insights (NEW)
                if (res.queryInsights || res.QueryInsights) {
                    newMessage.queryInsights = res.queryInsights || res.QueryInsights;
                    newMessage.showInsights = false; // Initially collapsed
                }

                this.messages.push(newMessage);

                this.confirmedQuestion = this.lastQuestion;
                this.confirmedSql = this.lastSql;
                this.lastQuestion = currentQ;
                this.lastSql = sql;

                this.cdr.detectChanges();
            },
            error: (err) => {
                this.loading = false;
                console.error('❌ [SendQuestion] Error:', err);

                let errorMsg = err.message;
                let sql = undefined;
                let queryLogId = undefined;

                if (err.error && typeof err.error === 'object') {
                    if (err.error.sql) sql = err.error.sql;
                    if (err.error.error) errorMsg = err.error.error;
                    if (err.error.queryLogId) queryLogId = err.error.queryLogId;
                }

                this.messages.push({
                    isAi: true,
                    text: 'Error: ' + errorMsg,
                    originalQuestion: currentQ,
                    retryCount: retryCount,
                    sql: sql,
                    queryLogId: queryLogId
                });
                this.cdr.detectChanges();
            }
        });
    }

    dislikeResponse(msg: ChatMessage) {
        if (!msg.originalQuestion) {
            alert('لا يمكن الإبلاغ عن هذه الرسالة (لا يوجد سؤال أصلي)');
            return;
        }

        this.loading = true;
        const retryCount = (msg.retryCount || 0) + 1;

        this.lastQuestion = this.confirmedQuestion;
        this.lastSql = this.confirmedSql;

        const userComment = prompt("هل يمكنك إخبارنا ما الخطأ في الإجابة؟ (اختياري)", "") || undefined;

        const request = {
            question: msg.originalQuestion,
            cacheId: msg.cacheId,
            sql: msg.sql,
            retryCount: retryCount,
            tenantId: 'default',
            companyId: 1,
            displayedMessage: msg.text,
            queryLogId: msg.queryLogId,
            userComment: userComment
        };

        this.apiService.dislike(request).subscribe({
            next: (res) => {
                this.loading = false;

                if (res.askForRetry) {
                    msg.isLogged = true;
                    if (retryCount > 2) {
                        this.messages.push({
                            isAi: true,
                            text: 'تم تسجيل المشكلة وسنعمل على حلها. يرجى محاولة طرح السؤال بطريقة أخرى.',
                            isLogged: true,
                            originalQuestion: msg.originalQuestion
                        });
                    } else {
                        this.messages.push({
                            isAi: true,
                            text: res.message,
                            isLogged: true,
                            askRetry: true,
                            originalQuestion: msg.originalQuestion,
                            retryCount: retryCount,
                            suggestionTips: res.suggestionTips
                        });
                    }
                }
                else if (res.shouldStopRetrying) {
                    msg.isLogged = true;
                    msg.retryCount = 2;
                    this.messages.push({
                        isAi: true,
                        text: res.message,
                        isLogged: true,
                        suggestionTips: res.suggestionTips
                    });
                } else if (res.regeneratedData) {
                    const index = this.messages.indexOf(msg);
                    if (index > -1) {
                        msg.retryCount = retryCount;
                    }
                    const newData = res.regeneratedData;
                    if (Array.isArray(newData)) {
                        this.messages.push({ isAi: true, tableData: newData, originalQuestion: msg.originalQuestion, retryCount: retryCount });
                    } else if (newData.Message) {
                        this.messages.push({ isAi: true, text: newData.Message, originalQuestion: msg.originalQuestion, retryCount: retryCount });
                    } else {
                        this.messages.push({ isAi: true, data: newData, originalQuestion: msg.originalQuestion, retryCount: retryCount });
                    }
                } else if (res.success) {
                    msg.isLogged = true;
                    if (res.message) {
                        this.messages.push({ isAi: true, text: res.message, isLogged: true, suggestionTips: res.suggestionTips });
                    }
                }
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.loading = false;
                msg.isLogged = true;
                this.messages.push({ isAi: true, text: 'تم تسجيل المشكلة للمراجعة ✅ (Error: ' + err.status + ')', isLogged: true });
                this.cdr.detectChanges();
            }
        });
    }

    likeResponse(msg: ChatMessage) {
        if (!msg.queryLogId) {
            msg.isLiked = true;
            this.cdr.detectChanges();
            return;
        }

        this.apiService.like(msg.queryLogId, 1).subscribe({
            next: (res) => {
                msg.isLiked = true;
                msg.isLogged = true;
                this.messages.push({ isAi: true, text: res.message || 'شكراً لتقييمك! 🙏', isLogged: true });
                this.cdr.detectChanges();
            },
            error: (err) => {
                msg.isLiked = true;
                this.cdr.detectChanges();
            }
        });
    }

    copySql(sql: string) {
        navigator.clipboard.writeText(sql).then(() => {
            console.log('✅ SQL copied to clipboard');
        }).catch(err => {
            console.error('❌ Failed to copy SQL:', err);
        });
    }


}
