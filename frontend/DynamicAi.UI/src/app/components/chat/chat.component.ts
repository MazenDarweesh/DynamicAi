import {
    Component,
    OnInit,
    OnDestroy,
    ViewChild,
    ElementRef,
    AfterViewChecked,
    ChangeDetectorRef,
    inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription, race } from 'rxjs';
import * as xlsx from 'xlsx';

import { ApiService } from '../../services/api.service';
import { ChatSignalService } from '../../services/chat-signal.service';
import { PocSettingsService } from '../../services/poc-settings.service';
import { ChatMessage, DirectedPageAskRequest, ShippedPageContext } from '../../models';
import { PAGE_244_PRESET_CONTEXT } from '../../presets/page244.preset';

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
    private pocSettings = inject(PocSettingsService);

    status = 'Connected! ✅';
    connectionState: 'connected' | 'checking' | 'error' = 'connected';
    errorMsg = '';

    question = '';
    messages: ChatMessage[] = [];
    loading = false;

    // Stop Generation & Reset
    private activeSubscription: Subscription | null = null;
    private resetSubscription: Subscription | null = null;

    // Conversation Context
    lastQuestion: string | undefined = undefined;
    lastSql: string | undefined = undefined;
    confirmedQuestion: string | undefined = undefined;
    confirmedSql: string | undefined = undefined;

    // POC Context Settings Panel
    showSettingsPanel = false;
    selectedPreset: 'page244' | 'custom' = 'page244';
    comID = 1;
    tenantId = 'default';
    tenantKey = '';
    connectionString = '';
    explicitContextJson = '';
    contextJsonError = '';
    parsedContext: ShippedPageContext | null = null;
    clearingCache = false;
    cacheActionMessage = '';
    cacheActionError = false;

    private readonly formattedPage244Preset = JSON.stringify(PAGE_244_PRESET_CONTEXT, null, 2);

    ngOnInit() {
        this.resetSubscription = this.chatSignal.reset$.subscribe(() => {
            this.resetChat();
        });

        // Load Persistent Chat State
        const savedState = this.chatSignal.getState();
        if (savedState.messages.length > 0) {
            this.messages = savedState.messages;
            this.lastQuestion = savedState.lastQuestion;
            this.lastSql = savedState.lastSql;
            this.confirmedQuestion = savedState.confirmedQuestion;
            this.confirmedSql = savedState.confirmedSql;
        }

        // Load Persistent POC Context Settings
        const snapshot = this.pocSettings.getSnapshot();
        this.comID = snapshot.comID ?? 1;
        this.tenantId = snapshot.tenantId || 'default';
        this.tenantKey = snapshot.tenantKey || '';
        this.connectionString = snapshot.connectionString || '';

        if (snapshot.explicitContextJson && snapshot.explicitContextJson.trim().length > 0) {
            this.explicitContextJson = snapshot.explicitContextJson;
            this.selectedPreset =
                snapshot.explicitContextJson.trim() === this.formattedPage244Preset.trim()
                    ? 'page244'
                    : 'custom';
        } else {
            this.explicitContextJson = this.formattedPage244Preset;
            this.selectedPreset = 'page244';
            this.pocSettings.save({ explicitContextJson: this.explicitContextJson });
        }

        this.validateContextJson(this.explicitContextJson);
        this.checkConnection();
    }

    ngOnDestroy() {
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

    ngAfterViewChecked() {
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

    onPresetChange(preset: 'page244' | 'custom'): void {
        this.selectedPreset = preset;
        if (preset === 'page244') {
            this.explicitContextJson = this.formattedPage244Preset;
            this.validateContextJson(this.explicitContextJson);
            this.pocSettings.save({ explicitContextJson: this.explicitContextJson });
        }
    }

    resetPreset(): void {
        this.selectedPreset = 'page244';
        this.explicitContextJson = this.formattedPage244Preset;
        this.cacheActionMessage = '';
        this.cacheActionError = false;
        this.validateContextJson(this.explicitContextJson);
        this.pocSettings.save({ explicitContextJson: this.explicitContextJson });
    }

    onSettingsFieldChange(): void {
        const normalizedComId = Number.isFinite(Number(this.comID)) ? Number(this.comID) : 1;
        this.pocSettings.save({
            comID: normalizedComId,
            tenantId: this.tenantId || 'default',
            tenantKey: this.tenantKey,
            connectionString: this.connectionString
        });
    }

    onContextJsonChange(value: string): void {
        this.explicitContextJson = value;
        this.selectedPreset =
            value.trim() === this.formattedPage244Preset.trim() ? 'page244' : 'custom';
        this.validateContextJson(value);
        this.pocSettings.save({ explicitContextJson: value });
    }

    private validateContextJson(rawJson: string): void {
        if (!rawJson || !rawJson.trim()) {
            this.parsedContext = null;
            this.contextJsonError = 'ExplicitContext JSON is required.';
            return;
        }

        try {
            const parsed = JSON.parse(rawJson);
            if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
                this.parsedContext = null;
                this.contextJsonError = 'ExplicitContext must be a JSON object.';
                return;
            }
            if (typeof parsed.pageId !== 'number' || !Number.isFinite(parsed.pageId)) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "pageId" must be a valid number.';
                return;
            }
            if (typeof parsed.pageName !== 'string' || !parsed.pageName.trim()) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "pageName" must be a non-empty string.';
                return;
            }
            if (
                !parsed.masterSetup ||
                typeof parsed.masterSetup !== 'object' ||
                typeof parsed.masterSetup.tableName !== 'string' ||
                !parsed.masterSetup.tableName.trim()
            ) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "masterSetup.tableName" is required.';
                return;
            }
            if (
                typeof parsed.masterSetup.primaryKey !== 'string' ||
                !parsed.masterSetup.primaryKey.trim()
            ) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "masterSetup.primaryKey" is required.';
                return;
            }
            if (!Array.isArray(parsed.masterSetup.columns)) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "masterSetup.columns" must be an array.';
                return;
            }
            if (!Array.isArray(parsed.detailSetups)) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "detailSetups" must be an array.';
                return;
            }

            this.parsedContext = parsed as ShippedPageContext;
            this.contextJsonError = '';
        } catch (err: any) {
            this.parsedContext = null;
            this.contextJsonError = `Invalid JSON: ${err?.message || 'Syntax error'}`;
        }
    }

    clearPageCache(): void {
        if (!this.parsedContext?.pageId) {
            this.cacheActionMessage = '⚠️ يرجى إدخال ExplicitContext JSON صالح يحتوي على pageId.';
            this.cacheActionError = true;
            return;
        }

        const targetPageId = this.parsedContext.pageId;
        this.clearingCache = true;
        this.cacheActionMessage = '';
        this.cacheActionError = false;

        this.apiService.clearCacheByPage(targetPageId).subscribe({
            next: (res) => {
                this.clearingCache = false;
                this.cacheActionError = false;
                this.cacheActionMessage =
                    res?.message || `✅ تم مسح الكاش للصفحة (${targetPageId}) بنجاح.`;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.clearingCache = false;
                this.cacheActionError = true;
                this.cacheActionMessage =
                    err?.error?.detail ||
                    err?.error?.message ||
                    `❌ فشل مسح الكاش للصفحة (${targetPageId}).`;
                this.cdr.detectChanges();
            }
        });
    }

    checkConnection() {
        this.connectionState = 'checking';
        this.status = 'جاري فحص النظام...';
        this.errorMsg = '';

        const timeoutId = setTimeout(() => {
            if (this.connectionState === 'checking') {
                this.connectionState = 'error';
                this.status = 'فشل الاتصال ❌';
                this.errorMsg = '⏱️ الخادم لم يستجب خلال 15 ثانية. قد يكون النظام في مرحلة التشغيل الأولى.';
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
                const problem = err?.error;
                this.errorMsg =
                    problem?.detail ||
                    (err?.status === 0
                        ? '⚠️ تعذر الاتصال بالخادم. يرجى التحقق من تشغيل الـ Backend.'
                        : '⚠️ الخادم غير متاح حالياً. يرجى المحاولة بعد قليل.');
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
        this.chatSignal.clearState();
        this.cdr.detectChanges();
    }

    getKeys(obj: any): string[] {
        return obj ? Object.keys(obj) : [];
    }

    hasTelemetry(msg: ChatMessage): boolean {
        return (
            msg.isCached !== undefined ||
            msg.inputTokens !== undefined ||
            msg.outputTokens !== undefined ||
            msg.cachedTokens !== undefined ||
            msg.llmCostUsd !== undefined
        );
    }

    formatCostUsd(cost: number | undefined): string {
        if (cost === undefined || cost === null || Number.isNaN(cost)) {
            return '$0.0000';
        }
        return `$${cost.toFixed(cost > 0 && cost < 0.0001 ? 6 : 4)}`;
    }

    formatText(text: string): string {
        if (!text) return '';

        return text
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/^#{3}\s+(.*$)/gm, '<h3>$1</h3>')
            .replace(/^#{2}\s+(.*$)/gm, '<h2>$1</h2>')
            .replace(/^#\s+(.*$)/gm, '<h1>$1</h1>')
            .replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>')
            .replace(/\*(.*?)\*/g, '<em>$1</em>')
            .replace(/```([\s\S]*?)```/g, '<pre class="code-block">$1</pre>')
            .replace(/`([^`]+)`/g, '<code class="inline-code">$1</code>')
            .replace(/^\s*-\s+(.*$)/gm, '• $1<br>')
            .replace(/\n/g, '<br>');
    }

    sendQuestion(retryCount: number = 0) {
        if (!this.question.trim()) return;

        if (!this.parsedContext) {
            this.showSettingsPanel = true;
            this.cdr.detectChanges();
            return;
        }

        const currentQ = this.question;
        this.messages.push({ isAi: false, text: currentQ });
        this.question = '';
        this.loading = true;
        this.cdr.detectChanges();

        const sessionId = this.chatSignal.getOrCreateSessionId();

        // Safety net: timeout after 60 seconds
        const timeout$ = new Observable<never>((observer) => {
            const timer = setTimeout(() => {
                observer.error(new Error('Timeout: Request took too long (>60s).'));
            }, 60000);
            return () => clearTimeout(timer);
        });

        const request: DirectedPageAskRequest = {
            question: currentQ,
            tenantId: this.tenantId.trim() || 'default',
            tenantKey: this.tenantKey.trim() || null,
            comID: Number.isFinite(Number(this.comID)) ? Number(this.comID) : 1,
            sessionId: sessionId,
            pageId: this.parsedContext.pageId,
            connectionString: this.connectionString.trim() || null,
            previousQuestion: this.lastQuestion ?? null,
            previousSql: this.lastSql ?? null,
            explicitContext: this.parsedContext
        };

        this.activeSubscription = race(
            this.apiService.directedPageAsk(request),
            timeout$
        ).subscribe({
            next: (res: any) => {
                this.loading = false;

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

                const getProp = (obj: any, key: string) =>
                    obj &&
                    (obj[key] ||
                        obj[key.toLowerCase()] ||
                        obj[key.charAt(0).toUpperCase() + key.slice(1)]);

                let newMessage: ChatMessage;
                const intent = res?.intent ?? res?.Intent;
                const isRichTextIntent = ['Prediction', 'Chat'].includes(intent);
                const isPredictionWithDataIntent = [
                    'PredictionWithDataRetrieval',
                    'PredictionWithDataFetch'
                ].includes(intent);

                if (typeof data === 'string' && !isRichTextIntent) {
                    try {
                        const parsed = JSON.parse(data);
                        if (typeof parsed === 'object') data = parsed;
                    } catch {
                        // Keep as string
                    }
                }

                const msgType = getProp(data, 'Type');
                const msgText = getProp(data, 'Message');

                if (isRichTextIntent) {
                    let content = '';
                    if (Array.isArray(data) && data.length > 0) {
                        const firstItem = data[0];
                        const prediction =
                            firstItem.prediction ||
                            firstItem.Prediction ||
                            firstItem.Message ||
                            firstItem.message;
                        content = prediction || JSON.stringify(data, null, 2);
                    } else if (typeof data === 'string') {
                        content = data;
                    } else if (msgText) {
                        content = msgText;
                    } else if (data && typeof data === 'object') {
                        content = JSON.stringify(data, null, 2);
                    }

                    newMessage = {
                        isAi: true,
                        text: content,
                        originalQuestion: currentQ,
                        retryCount: retryCount,
                        isRichText: true
                    };
                } else if (isPredictionWithDataIntent) {
                    const insights = res?.queryInsights || res?.QueryInsights;
                    const predictionText = insights?.Reasoning || insights?.reasoning || msgText || '';

                    newMessage = {
                        isAi: true,
                        text: predictionText,
                        tableData: Array.isArray(data) && data.length > 0 ? data : undefined,
                        isRichText: true,
                        originalQuestion: currentQ,
                        retryCount: retryCount
                    };
                } else if (msgText && !msgType) {
                    newMessage = {
                        isAi: true,
                        text: msgText,
                        originalQuestion: currentQ,
                        retryCount: retryCount
                    };
                } else if (
                    msgType === 'Question' ||
                    res?.extensions?.type === 'Question' ||
                    res?.extensions?.isAmbiguous
                ) {
                    const clarificationText = msgText || res?.detail || res?.extensions?.detail;
                    newMessage = {
                        isAi: true,
                        text: clarificationText,
                        originalQuestion: currentQ,
                        retryCount: retryCount
                    };
                } else if (Array.isArray(data)) {
                    if (data.length > 0) {
                        newMessage = {
                            isAi: true,
                            tableData: data,
                            originalQuestion: currentQ,
                            retryCount: retryCount
                        };
                    } else {
                        newMessage = {
                            isAi: true,
                            text: 'No results found.',
                            originalQuestion: currentQ,
                            retryCount: retryCount
                        };
                    }
                } else if (typeof data === 'string') {
                    newMessage = {
                        isAi: true,
                        text: data,
                        originalQuestion: currentQ,
                        retryCount: retryCount
                    };
                } else {
                    newMessage = {
                        isAi: true,
                        data: data,
                        originalQuestion: currentQ,
                        retryCount: retryCount
                    };
                }

                newMessage.sql = sql;
                newMessage.cacheId = cacheId;
                newMessage.isLogged = false;
                newMessage.queryLogId =
                    res?.queryLogId || res?.QueryLogId || res?.extensions?.queryLogId;
                newMessage.intent = intent;

                // Telemetry fields
                const inputTokens = res?.inputTokens ?? res?.InputTokens;
                const outputTokens = res?.outputTokens ?? res?.OutputTokens;
                const cachedTokens = res?.cachedTokens ?? res?.CachedTokens;
                const llmCostUsd = res?.llmCostUsd ?? res?.LlmCostUsd;
                const isCached = res?.isCached ?? res?.IsCached;

                if (typeof inputTokens === 'number') newMessage.inputTokens = inputTokens;
                if (typeof outputTokens === 'number') newMessage.outputTokens = outputTokens;
                if (typeof cachedTokens === 'number') newMessage.cachedTokens = cachedTokens;
                if (typeof llmCostUsd === 'number') newMessage.llmCostUsd = llmCostUsd;
                if (typeof isCached === 'boolean') newMessage.isCached = isCached;

                if (res?.queryInsights || res?.QueryInsights) {
                    newMessage.queryInsights = res.queryInsights || res.QueryInsights;
                    (newMessage as any).showInsights = false;
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

                if (err.message && err.message.includes('Timeout')) {
                    errorMsg =
                        '⏱️ المعالج استغرق وقتاً طويلاً جداً (أكثر من 60 ثانية). يرجى محاولة صياغة السؤال بشكل أبسط.';
                } else if (err.status === 0) {
                    errorMsg =
                        '⚠️ لا يمكن الاتصال بالخادم. يرجى التحقق من اتصال الإنترنت والمحاولة مرة أخرى.';
                } else if (err.error && typeof err.error === 'object' && err.error.detail) {
                    errorMsg = err.error.detail;
                    sql = err.error.extensions?.sql ?? err.error.sql ?? undefined;
                    queryLogId =
                        err.error.extensions?.queryLogId ?? err.error.queryLogId ?? undefined;
                } else if (err.error && typeof err.error === 'object') {
                    sql = err.error.sql ?? undefined;
                    errorMsg =
                        err.error.error ??
                        err.error.Error ??
                        err.error.message ??
                        '⚠️ حدث خطأ أثناء معالجة طلبك. يرجى المحاولة مرة أخرى.';
                    queryLogId = err.error.queryLogId ?? undefined;
                } else if (typeof err.error === 'string' && err.error.length > 0) {
                    errorMsg = err.error;
                } else {
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

        const userComment =
            prompt('هل يمكنك إخبارنا ما الخطأ في الإجابة؟ (اختياري)', '') || undefined;
        const sessionId = this.chatSignal.getOrCreateSessionId();

        const request = {
            question: msg.originalQuestion,
            sessionId: sessionId,
            cacheId: msg.cacheId,
            sql: msg.sql,
            retryCount: retryCount,
            tenantId: this.tenantId.trim() || 'default',
            companyId: Number.isFinite(Number(this.comID)) ? Number(this.comID) : 1,
            displayedMessage: msg.text,
            queryLogId: msg.queryLogId,
            userComment: userComment,
            pageId: this.parsedContext?.pageId
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
                } else if (res.shouldStopRetrying) {
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
                } else if (res.success) {
                    msg.isLogged = true;
                    if (res.message) {
                        this.messages.push({
                            isAi: true,
                            text: res.message,
                            isLogged: true,
                            suggestionTips: res.suggestionTips
                        });
                    }
                }
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.loading = false;
                msg.isLogged = true;
                const errText =
                    err.status === 0
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

        const companyId = Number.isFinite(Number(this.comID)) ? Number(this.comID) : 1;
        this.apiService.like(msg.queryLogId, companyId).subscribe({
            next: (res) => {
                msg.isLiked = true;
                msg.isLogged = true;
                this.messages.push({
                    isAi: true,
                    text: res.message || 'شكراً لتقييمك! 🙏',
                    isLogged: true
                });
                this.cdr.detectChanges();
            },
            error: () => {
                msg.isLiked = true;
                this.cdr.detectChanges();
            }
        });
    }

    copySql(sql: string) {
        navigator.clipboard
            .writeText(sql)
            .then(() => {
                console.log('✅ SQL copied to clipboard');
            })
            .catch((err) => {
                console.error('❌ Failed to copy SQL:', err);
            });
    }

    printTable(msg: ChatMessage) {
        if (!msg.tableData || msg.tableData.length === 0) return;

        const printWindow = window.open('', '', 'height=800,width=1000');
        if (!printWindow) return;

        const timestamp = new Date().toLocaleString('en-EG', {
            dateStyle: 'medium',
            timeStyle: 'short'
        });
        const keys = this.getKeys(msg.tableData[0]);
        const logoUrl = '/squareLogo.jpeg';

        const tableHtml = `
        <html>
        <head>
            <title>Dynamic AI - Print Data</title>
            <style>
                @page { margin: 0; size: auto; }
                body { font-family: 'Segoe UI', Arial, sans-serif; padding: 40px; color: #333; margin: 0; }
                .content-wrapper { padding: 40px; }
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
                @media print { .no-print { display: none; } }
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
                <div class="question-box">${msg.originalQuestion || 'No question provided'}</div>
                <h2>Data Result</h2>
                <table>
                    <thead>
                        <tr>${keys.map((k) => `<th>${k}</th>`).join('')}</tr>
                    </thead>
                    <tbody>
                        ${msg.tableData
                            .map(
                                (row) => `
                            <tr>${keys.map((k) => `<td>${row[k]}</td>`).join('')}</tr>
                        `
                            )
                            .join('')}
                    </tbody>
                </table>
            </div>
            <script>
                window.onload = function() {
                    window.focus();
                    setTimeout(function() { window.print(); }, 500);
                };
                window.onafterprint = function() { window.close(); };
            </script>
        </body>
        </html>`;

        printWindow.document.write(tableHtml);
        printWindow.document.close();
    }

    async exportExcel(msg: ChatMessage) {
        if (!msg.tableData || msg.tableData.length === 0) return;

        try {
            const worksheet = xlsx.utils.json_to_sheet(msg.tableData);
            const workbook = xlsx.utils.book_new();
            xlsx.utils.book_append_sheet(workbook, worksheet, 'Data');

            const timestamp = new Date().toISOString().slice(0, 19).replace(/:/g, '-');
            const sanitizedQ = (msg.originalQuestion || 'data')
                .replace(/[^a-z0-9]/gi, '_')
                .substring(0, 20);
            const fileName = `DynamicAI_${sanitizedQ}_${timestamp}.xlsx`;

            xlsx.writeFile(workbook, fileName);
        } catch (error) {
            console.error('❌ Export failed:', error);
            alert('Failed to export Excel. Please try again.');
        }
    }
}
