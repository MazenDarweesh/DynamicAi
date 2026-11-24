import {
    Component, OnInit, OnDestroy, ViewChild, ElementRef,
    AfterViewChecked, ChangeDetectorRef, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription, race } from 'rxjs';

import { ApiService } from '../../services/api.service';
import { ChatSignalService } from '../../services/chat-signal.service';
import { ChatExportService } from '../../services/chat-export.service';
import { ChatMessage, DirectedPageAskRequest } from '../../models';
import { PAGE_244_PRESET_CONTEXT } from '../../presets/page244.preset';
import { PocConnectionState, PocContextPanelComponent } from './poc-context-panel/poc-context-panel.component';
import { ChatMessageItemComponent } from './chat-message-item/chat-message-item.component';
import { mapDirectedAskError, mapDirectedAskResponse, mapDislikeOutcome } from '../../utils/chat-response.mapper';

@Component({
    selector: 'app-chat',
    standalone: true,
    imports: [CommonModule, FormsModule, PocContextPanelComponent, ChatMessageItemComponent],
    templateUrl: './chat.component.html',
    styleUrls: ['./chat.component.css']
})
export class ChatComponent implements OnInit, OnDestroy, AfterViewChecked {
    @ViewChild('scrollContainer') private scrollContainer!: ElementRef;

    private apiService = inject(ApiService);
    private cdr = inject(ChangeDetectorRef);
    private chatSignal = inject(ChatSignalService);
    private chatExport = inject(ChatExportService);

    status = 'جاري فحص النظام...';
    connectionState: 'connected' | 'checking' | 'error' = 'checking';
    errorMsg = '';

    question = '';
    messages: ChatMessage[] = [];
    loading = false;

    private activeSubscription: Subscription | null = null;
    private resetSubscription: Subscription | null = null;

    lastQuestion: string | undefined;
    lastSql: string | undefined;
    confirmedQuestion: string | undefined;
    confirmedSql: string | undefined;

    showSettingsPanel = false;
    contextValidationMessage = '';
    pocState: PocConnectionState = {
        parsedContext: PAGE_244_PRESET_CONTEXT,
        contextJsonError: '',
        comID: 1,
        tenantId: 'default',
        tenantKey: '',
        connectionString: ''
    };

    ngOnInit(): void {
        this.resetSubscription = this.chatSignal.reset$.subscribe(() => this.resetChat());

        const saved = this.chatSignal.getState();
        if (saved.messages.length > 0) {
            this.messages = saved.messages;
            this.lastQuestion = saved.lastQuestion;
            this.lastSql = saved.lastSql;
            this.confirmedQuestion = saved.confirmedQuestion;
            this.confirmedSql = saved.confirmedSql;
        }

        this.checkConnection();
    }

    ngOnDestroy(): void {
        this.resetSubscription?.unsubscribe();
        this.activeSubscription?.unsubscribe();
        this.chatSignal.saveState(
            this.messages,
            this.lastQuestion,
            this.lastSql,
            this.confirmedQuestion,
            this.confirmedSql
        );
    }

    ngAfterViewChecked(): void {
        this.scrollToBottom();
    }

    scrollToBottom(): void {
        try {
            this.scrollContainer.nativeElement.scrollTop =
                this.scrollContainer.nativeElement.scrollHeight;
        } catch {
            // Ignore when scrollContainer is not yet rendered
        }
    }

    toggleSettingsPanel(): void {
        this.showSettingsPanel = !this.showSettingsPanel;
    }

    onContextChanged(state: PocConnectionState): void {
        this.pocState = state;
        if (state.connectionString.trim() || state.tenantKey.trim()) {
            this.contextValidationMessage = '';
        }
        this.cdr.detectChanges();
    }

    checkConnection(): void {
        this.connectionState = 'checking';
        this.status = 'جاري فحص النظام...';
        this.errorMsg = '';

        const timeoutId = setTimeout(() => {
            if (this.connectionState === 'checking') {
                this.connectionState = 'error';
                this.status = 'فشل الاتصال ❌';
                this.errorMsg =
                    '⏱️ الخادم لم يستجب خلال 15 ثانية. قد يكون النظام في مرحلة التشغيل الأولى.';
                this.cdr.detectChanges();
            }
        }, 15000);

        this.apiService.testConnection().subscribe({
            next: () => {
                clearTimeout(timeoutId);
                this.connectionState = 'connected';
                this.status = 'متصل ✅';
                this.errorMsg = '';
                this.cdr.detectChanges();
            },
            error: (err) => {
                clearTimeout(timeoutId);
                this.connectionState = 'error';
                this.status = 'فشل الاتصال ❌';
                this.errorMsg =
                    err?.error?.detail ||
                    (err?.status === 0
                        ? '⚠️ تعذر الاتصال بالخادم. يرجى التحقق من تشغيل الـ Backend.'
                        : '⚠️ الخادم غير متاح حالياً. يرجى المحاولة بعد قليل.');
                this.cdr.detectChanges();
            }
        });
    }

    onEnter(event: any): void {
        if (!event.shiftKey) {
            event.preventDefault();
            this.sendQuestion();
        }
    }

    setQuestion(q: string, retryCount: number = 0): void {
        this.question = q;
        this.sendQuestion(retryCount);
    }

    resetChat(): void {
        this.messages = [];
        this.lastQuestion = undefined;
        this.lastSql = undefined;
        this.confirmedQuestion = undefined;
        this.confirmedSql = undefined;
        this.chatSignal.clearState();
        this.cdr.detectChanges();
    }

    sendQuestion(retryCount: number = 0): void {
        if (!this.question.trim()) return;

        if (!this.pocState.parsedContext) {
            this.showSettingsPanel = true;
            this.cdr.detectChanges();
            return;
        }

        if (!this.pocState.connectionString.trim() && !this.pocState.tenantKey.trim()) {
            this.showSettingsPanel = true;
            this.contextValidationMessage =
                '⚠️ يرجى إدخال ConnectionString (أو TenantKey) في لوحة إعدادات السياق أعلاه قبل إرسال السؤال.';
            this.cdr.detectChanges();
            return;
        }

        this.contextValidationMessage = '';
        const currentQ = this.question;
        this.messages.push({ isAi: false, text: currentQ });
        this.question = '';
        this.loading = true;
        this.cdr.detectChanges();

        const timeout$ = new Observable<never>((observer) => {
            const timer = setTimeout(
                () => observer.error(new Error('Timeout: Request took too long (>60s).')),
                60000
            );
            return () => clearTimeout(timer);
        });

        const request: DirectedPageAskRequest = {
            question: currentQ,
            tenantId: this.pocState.tenantId.trim() || 'default',
            tenantKey: this.pocState.tenantKey.trim() || null,
            comID: this.normalizedComId(),
            sessionId: this.chatSignal.getOrCreateSessionId(),
            pageId: this.pocState.parsedContext.pageId,
            connectionString: this.pocState.connectionString.trim() || null,
            previousQuestion: this.lastQuestion ?? null,
            previousSql: this.lastSql ?? null,
            explicitContext: this.pocState.parsedContext
        };

        this.activeSubscription = race(this.apiService.directedPageAsk(request), timeout$).subscribe({
            next: (res: any) => {
                this.loading = false;
                const mapped = mapDirectedAskResponse(res, currentQ, retryCount);
                this.messages.push(mapped.message);

                this.confirmedQuestion = this.lastQuestion;
                this.confirmedSql = this.lastSql;
                this.lastQuestion = currentQ;
                this.lastSql = mapped.sql;

                this.activeSubscription = null;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.loading = false;
                console.error('❌ [SendQuestion] Error:', err);
                this.messages.push(mapDirectedAskError(err, currentQ, retryCount));
                this.activeSubscription = null;
                this.cdr.detectChanges();
            }
        });
    }

    stopGeneration(): void {
        this.activeSubscription?.unsubscribe();
        this.activeSubscription = null;
        this.loading = false;
        this.messages.push({ isAi: true, text: '⛔ تم إيقاف التوليد بواسطة المستخدم.', isLogged: true });
        this.cdr.detectChanges();
    }

    dislikeResponse(msg: ChatMessage): void {
        if (!msg.originalQuestion) {
            alert('لا يمكن الإبلاغ عن هذه الرسالة (لا يوجد سؤال أصلي)');
            return;
        }

        this.loading = true;
        const retryCount = (msg.retryCount || 0) + 1;
        this.lastQuestion = this.confirmedQuestion;
        this.lastSql = this.confirmedSql;
        const userComment = prompt('هل يمكنك إخبارنا ما الخطأ في الإجابة؟ (اختياري)', '') || undefined;

        this.apiService.dislike({
            question: msg.originalQuestion,
            sessionId: this.chatSignal.getOrCreateSessionId(),
            cacheId: msg.cacheId,
            sql: msg.sql,
            retryCount,
            tenantId: this.pocState.tenantId.trim() || 'default',
            companyId: this.normalizedComId(),
            displayedMessage: msg.text,
            queryLogId: msg.queryLogId,
            userComment,
            pageId: this.pocState.parsedContext?.pageId
        }).subscribe({
            next: (res) => {
                this.loading = false;
                const followUp = mapDislikeOutcome(msg, res, retryCount);
                if (followUp) this.messages.push(followUp);
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

    likeResponse(msg: ChatMessage): void {
        if (!msg.queryLogId) {
            msg.isLiked = true;
            this.cdr.detectChanges();
            return;
        }

        this.apiService.like(msg.queryLogId, this.normalizedComId()).subscribe({
            next: (res) => {
                msg.isLiked = true;
                msg.isLogged = true;
                this.messages.push({ isAi: true, text: res.message || 'شكراً لتقييمك! 🙏', isLogged: true });
                this.cdr.detectChanges();
            },
            error: () => {
                msg.isLiked = true;
                this.cdr.detectChanges();
            }
        });
    }

    copySql(sql: string): void {
        navigator.clipboard.writeText(sql).catch((err) => {
            console.error('❌ Failed to copy SQL:', err);
        });
    }

    printTable(msg: ChatMessage): void {
        this.chatExport.printTable(msg);
    }

    exportExcel(msg: ChatMessage): Promise<void> {
        return this.chatExport.exportExcel(msg);
    }

    private normalizedComId(): number {
        const parsed = Number(this.pocState.comID);
        return Number.isFinite(parsed) ? parsed : 1;
    }
}
