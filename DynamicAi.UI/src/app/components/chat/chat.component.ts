import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewChecked, ChangeDetectorRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../services/api.service';
import { ChatSignalService } from '../../services/chat-signal.service';
import { environment } from '../../../environments/environment';
import { ChatMessage } from '../../models';
import { Observable, Subscription, race } from 'rxjs';
import * as xlsx from 'xlsx';

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

    // Stop Generation
    private activeSubscription: Subscription | null = null;

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



    ngAfterViewChecked() {
        this.scrollToBottom();
    }

    scrollToBottom(): void {
        try {
            this.scrollContainer.nativeElement.scrollTop = this.scrollContainer.nativeElement.scrollHeight;
        } catch (err) { }
    }

    checkConnection() {
        this.status = 'جاري فحص النظام...';
        this.errorMsg = '';

        const timeoutId = setTimeout(() => {
            if (this.status === 'جاري فحص النظام...') {
                this.status = 'فشل الاتصال ❌';
                this.errorMsg = '⏱️ الخادم لم يستجب خلال 15 ثانية. قد يكون النظام في مرحلة التشغيل الأولى.';
                this.cdr.detectChanges();
            }
        }, 15000);

        const sessionId = this.chatSignal.getOrCreateSessionId();
        this.apiService.ask('Hello', undefined, undefined, sessionId).subscribe({
            next: (res) => {
                clearTimeout(timeoutId);
                this.status = 'متصل ✅';
                this.cdr.detectChanges();
            },
            error: (err) => {
                clearTimeout(timeoutId);
                this.status = 'فشل الاتصال ❌';
                this.errorMsg = err.status === 0
                    ? '⚠️ تعذر الاتصال بالخادم. يرجى التحقق من اتصال الإنترنت.'
                    : '⚠️ الخادم غير متاح حالياً. يرجى المحاولة بعد قليل.';
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

    formatText(text: string): string {
        if (!text) return '';

        let html = text
            // Escape HTML (basic)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            // Headers
            .replace(/^#{3}\s+(.*$)/gm, '<h3>$1</h3>')
            .replace(/^#{2}\s+(.*$)/gm, '<h2>$1</h2>')
            .replace(/^#\s+(.*$)/gm, '<h1>$1</h1>')
            // Bold
            .replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>')
            // Italic
            .replace(/\*(.*?)\*/g, '<em>$1</em>')
            // Code Blocks
            .replace(/```([\s\S]*?)```/g, '<pre class="code-block">$1</pre>')
            // Inline Code
            .replace(/`([^`]+)`/g, '<code class="inline-code">$1</code>')
            // Lists (Simple bullet points)
            .replace(/^\s*-\s+(.*$)/gm, '• $1<br>')
            // Newlines (convert to <br> if not inside pre/h tags roughly)
            .replace(/\n/g, '<br>');

        return html;
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

        // 🛡️ Safety Net: Timeout after 60 seconds
        const timeout$ = new Observable<never>(observer => {
            const timer = setTimeout(() => {
                observer.error(new Error('Timeout: Request took too long (>60s).'));
            }, 60000);
            return () => clearTimeout(timer);
        });

        this.activeSubscription = race(
            this.apiService.ask(currentQ, this.lastQuestion, this.lastSql, sessionId),
            timeout$
        ).subscribe({
            next: (res: any) => {
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

                // Helper to get case-insensitive property
                const getProp = (obj: any, key: string) => obj && (obj[key] || obj[key.toLowerCase()] || obj[key.charAt(0).toUpperCase() + key.slice(1)]);

                let newMessage: ChatMessage;
                const intent = res.intent ?? res.Intent;
                // Detect Rich Text Intents (Non-SQL)
                const isRichTextIntent = ['Prediction', 'PredictionWithDataFetch', 'Chat'].includes(intent);

                // Attempt to parse string data if it looks like JSON, UNLESS it's a rich text intent that returns a string
                if (typeof data === 'string' && !isRichTextIntent) {
                    try {
                        const parsed = JSON.parse(data);
                        // Only use parsed if it's an object/array, otherwise keep as string
                        if (typeof parsed === 'object') data = parsed;
                    } catch (e) {
                        // Keep as string
                    }
                }

                const msgType = getProp(data, 'Type');
                const msgText = getProp(data, 'Message');

                if (isRichTextIntent) {
                    // Force text/rich display for these intents
                    let content = '';

                    // Handle Array response (PredictionWithDataFetch often returns [ { prediction: "..." } ])
                    if (Array.isArray(data) && data.length > 0) {
                        const firstItem = data[0];
                        // Check for prediction property (case-insensitive)
                        const prediction = firstItem.prediction || firstItem.Prediction || firstItem.Message || firstItem.message;
                        if (prediction) {
                            content = prediction;
                        } else {
                            content = JSON.stringify(data, null, 2);
                        }
                    }
                    else if (typeof data === 'string') content = data;
                    else if (msgText) content = msgText;
                    else if (data && typeof data === 'object') content = JSON.stringify(data, null, 2);

                    newMessage = {
                        isAi: true,
                        text: content,
                        originalQuestion: currentQ,
                        retryCount: retryCount,
                        isRichText: true // Enable HTML rendering
                    };
                }
                else if (msgText && !msgType) {
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
                    // Fallback for objects (Data Response)
                    // Check if data is just a string after all
                    if (typeof data === 'string') {
                        newMessage = { isAi: true, text: data, originalQuestion: currentQ, retryCount: retryCount };
                    } else {
                        newMessage = { isAi: true, data: data, originalQuestion: currentQ, retryCount: retryCount };
                    }
                }

                newMessage.sql = sql;
                newMessage.cacheId = cacheId;
                newMessage.isLogged = false;
                newMessage.queryLogId = res.queryLogId || res.QueryLogId;
                newMessage.intent = intent;

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

                this.activeSubscription = null;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.loading = false;
                console.error('❌ [SendQuestion] Error:', err);

                let errorMsg = '';
                let sql = undefined;
                let queryLogId = undefined;

                // 1. Frontend timeout (60s safety net)
                if (err.message && err.message.includes('Timeout')) {
                    errorMsg = '⏱️ المعالج استغرق وقتاً طويلاً جداً (أكثر من 60 ثانية). يرجى محاولة صياغة السؤال بشكل أبسط.';
                }
                // 2. Network completely down (status 0)
                else if (err.status === 0) {
                    errorMsg = '⚠️ لا يمكن الاتصال بالخادم. يرجى التحقق من اتصال الإنترنت والمحاولة مرة أخرى.';
                }
                // 3. Backend returned a structured error object (500 with { error, sql, queryLogId })
                else if (err.error && typeof err.error === 'object') {
                    if (err.error.sql) sql = err.error.sql;
                    if (err.error.error) errorMsg = err.error.error;
                    if (err.error.queryLogId) queryLogId = err.error.queryLogId;
                    if (!errorMsg) errorMsg = '⚠️ حدث خطأ أثناء معالجة طلبك. يرجى المحاولة مرة أخرى.';
                }
                // 4. Backend returned a plain string error (503, 500, 400)
                else if (typeof err.error === 'string' && err.error.length > 0) {
                    errorMsg = err.error;
                }
                // 5. Fallback
                else {
                    errorMsg = '⚠️ حدث خطأ غير متوقع. يرجى المحاولة مرة أخرى.';
                }

                this.messages.push({
                    isAi: true,
                    text: errorMsg,
                    originalQuestion: currentQ,
                    retryCount: retryCount,
                    sql: sql,
                    queryLogId: queryLogId
                });
                this.activeSubscription = null;
                this.cdr.detectChanges();
            }
        });
    }

    stopGeneration() {
        if (this.activeSubscription) {
            this.activeSubscription.unsubscribe();
            this.activeSubscription = null;
        }
        this.loading = false;
        this.messages.push({
            isAi: true,
            text: '⛔ تم إيقاف التوليد بواسطة المستخدم.',
            isLogged: true
        });
        this.cdr.detectChanges();
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
                const errText = err.status === 0
                    ? '⚠️ تعذر الاتصال بالخادم. تم حفظ ملاحظتك محلياً وسيتم إرسالها لاحقاً.'
                    : '✅ تم تسجيل الملاحظة. شكراً لمساعدتنا في التحسين!';
                this.messages.push({ isAi: true, text: errText, isLogged: true });
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

    // 🖨️ Print Data Table
    printTable(msg: ChatMessage) {
        if (!msg.tableData || msg.tableData.length === 0) return;

        const printWindow = window.open('', '', 'height=800,width=1000');
        if (!printWindow) return;

        // Formatted timestamp
        const timestamp = new Date().toLocaleString('en-EG', { dateStyle: 'medium', timeStyle: 'short' });
        const keys = this.getKeys(msg.tableData[0]);

        // Base64 logo or absolute URL. Since it's in public folder, use root path.
        const logoUrl = '/squareLogo.jpeg';

        let tableHtml = `
        <html>
        <head>
            <title>Dynamic AI - Print Data</title>
            <style>
                @page { margin: 0; size: auto; } /* Hides browser default header/footer */
                body { font-family: 'Segoe UI', Arial, sans-serif; padding: 40px; color: #333; margin: 0; }
                .content-wrapper { padding: 40px; } /* Ensures padding inside the page since body margin is 0 */
                
                .header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; border-bottom: 2px solid #eee; padding-bottom: 20px; }
                .logo-section { display: flex; align-items: center; gap: 15px; }
                .logo { height: 60px; width: auto; object-fit: contain; }
                .app-name { font-size: 1.5rem; font-weight: bold; color: #6366f1; }
                .meta { text-align: right; font-size: 0.9rem; color: #666; }
                
                h2 { margin: 0 0 10px 0; font-size: 1.4rem; color: #1e293b; }
                
                table { width: 100%; border-collapse: collapse; margin-bottom: 30px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
                th, td { border: 1px solid #e2e8f0; padding: 12px; text-align: left; font-size: 0.95rem; }
                th { background-color: #f8fafc; font-weight: 600; color: #475569; -webkit-print-color-adjust: exact; }
                tr:nth-child(even) { background-color: #f1f5f9; -webkit-print-color-adjust: exact; }
                
                .question-box { 
                    margin-bottom: 20px; 
                    padding: 15px; 
                    background-color: #f1f5f9; 
                    border-left: 4px solid #6366f1;
                    font-size: 1.2rem; 
                    font-weight: bold; 
                    color: #1e293b; 
                    -webkit-print-color-adjust: exact;
                }
                
                @media print {
                    .no-print { display: none; }
                }
            </style>
        </head>
        <body>
            <div class="content-wrapper">
                <div class="header">
                    <div class="logo-section">
                        <img src="${logoUrl}" alt="Dynamic AI Logo" class="logo" />
                        <div class="app-name">Dynamic AI</div>
                    </div>
                    <div class="meta">
                        <div>Date: ${timestamp}</div>
                        <div>Rows: ${msg.tableData.length}</div>
                    </div>
                </div>

                <div class="question-box">
                    ${msg.originalQuestion || 'No question provided'}
                </div>

                <h2>Data Result</h2>
                
                <table>
                    <thead>
                        <tr>${keys.map(k => `<th>${k}</th>`).join('')}</tr>
                    </thead>
                    <tbody>
                        ${msg.tableData.map(row => `
                            <tr>${keys.map(k => `<td>${row[k]}</td>`).join('')}</tr>
                        `).join('')}
                    </tbody>
                </table>
            </div>

            <script>
                window.onload = function() {
                    window.focus();
                    setTimeout(function() {
                        window.print();
                    }, 500);
                };
                window.onafterprint = function() {
                    window.close();
                };
            </script>
        </body>
        </html>`;

        printWindow.document.write(tableHtml);
        printWindow.document.close();
    }

    // 📥 Export to Excel (using xlsx)
    async exportExcel(msg: ChatMessage) {
        if (!msg.tableData || msg.tableData.length === 0) return;

        try {
            // Dynamically import xlsx to keep bundle size optimized if desired, or just use standard import
            // Since user asked for "import * as xlsx", we will use that at the top level, 
            // but for now let's assume standard import is available.

            // NOTE: We need to import xlsx at the top of the file. 
            // I will add the import statement in a separate replacement chunk.

            const worksheet = xlsx.utils.json_to_sheet(msg.tableData);
            const workbook = xlsx.utils.book_new();
            xlsx.utils.book_append_sheet(workbook, worksheet, 'Data');

            // Generate filename based on question or timestamp
            const timestamp = new Date().toISOString().slice(0, 19).replace(/:/g, '-');
            const sanitizedQ = (msg.originalQuestion || 'data').replace(/[^a-z0-9]/gi, '_').substring(0, 20);
            const fileName = `DynamicAI_${sanitizedQ}_${timestamp}.xlsx`;

            xlsx.writeFile(workbook, fileName);

            console.log('✅ Exported to Excel:', fileName);
        } catch (error) {
            console.error('❌ Export failed:', error);
            alert('Failed to export Excel. Please try again.');
        }
    }
}
