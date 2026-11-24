import { ChatMessage } from '../models';

export function formatChatMarkdown(text: string): string {
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

export function hasMessageTelemetry(msg: ChatMessage): boolean {
    return (
        msg.isCached !== undefined ||
        msg.inputTokens !== undefined ||
        msg.outputTokens !== undefined ||
        msg.cachedTokens !== undefined ||
        msg.llmCostUsd !== undefined
    );
}

export function formatCostUsd(cost: number | undefined): string {
    if (cost === undefined || cost === null || Number.isNaN(cost)) {
        return '$0.0000';
    }
    return `$${cost.toFixed(cost > 0 && cost < 0.0001 ? 6 : 4)}`;
}

export function mapDirectedAskResponse(
    res: any,
    originalQuestion: string,
    retryCount: number
): { message: ChatMessage; sql?: string } {
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
            originalQuestion,
            retryCount,
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
            originalQuestion,
            retryCount
        };
    } else if (msgText && !msgType) {
        newMessage = {
            isAi: true,
            text: msgText,
            originalQuestion,
            retryCount
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
            originalQuestion,
            retryCount
        };
    } else if (Array.isArray(data)) {
        if (data.length > 0) {
            newMessage = {
                isAi: true,
                tableData: data,
                originalQuestion,
                retryCount
            };
        } else {
            newMessage = {
                isAi: true,
                text: 'No results found.',
                originalQuestion,
                retryCount
            };
        }
    } else if (typeof data === 'string') {
        newMessage = {
            isAi: true,
            text: data,
            originalQuestion,
            retryCount
        };
    } else {
        newMessage = {
            isAi: true,
            data,
            originalQuestion,
            retryCount
        };
    }

    newMessage.sql = sql;
    newMessage.cacheId = cacheId;
    newMessage.isLogged = false;
    newMessage.queryLogId =
        res?.queryLogId || res?.QueryLogId || res?.extensions?.queryLogId;
    newMessage.intent = intent;

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

    return { message: newMessage, sql };
}

export function mapDirectedAskError(
    err: any,
    originalQuestion: string,
    retryCount: number
): ChatMessage {
    let errorMsg = '';
    let sql: string | undefined = undefined;
    let queryLogId: string | undefined = undefined;

    if (err?.message && err.message.includes('Timeout')) {
        errorMsg =
            '⏱️ المعالج استغرق وقتاً طويلاً جداً (أكثر من 60 ثانية). يرجى محاولة صياغة السؤال بشكل أبسط.';
    } else if (err?.status === 0) {
        errorMsg =
            '⚠️ لا يمكن الاتصال بالخادم. يرجى التحقق من اتصال الإنترنت والمحاولة مرة أخرى.';
    } else if (err?.status === 401) {
        errorMsg =
            '🔑 مطلوب إدخال مفتاح X-Api-Key صالح في الشريط العلوي (وضع الحماية Strict مفعّل).';
    } else if (err?.status === 403) {
        errorMsg =
            '⛔ مفتاح X-Api-Key الحالي لا يملك صلاحية تنفيذ هذا الإجراء أو أن TenantKey غير مصرح به.';
    } else if (err?.error && typeof err.error === 'object' && err.error.detail) {
        errorMsg = err.error.detail;
        sql = err.error.extensions?.sql ?? err.error.sql ?? undefined;
        queryLogId =
            err.error.extensions?.queryLogId ?? err.error.queryLogId ?? undefined;
    } else if (err?.error && typeof err.error === 'object') {
        sql = err.error.sql ?? undefined;
        errorMsg =
            err.error.error ??
            err.error.Error ??
            err.error.message ??
            '⚠️ حدث خطأ أثناء معالجة طلبك. يرجى المحاولة مرة أخرى.';
        queryLogId = err.error.queryLogId ?? undefined;
    } else if (typeof err?.error === 'string' && err.error.length > 0) {
        errorMsg = err.error;
    } else {
        errorMsg = '⚠️ حدث خطأ غير متوقع. يرجى المحاولة مرة أخرى.';
    }

    return {
        isAi: true,
        text: errorMsg,
        originalQuestion,
        retryCount,
        sql,
        queryLogId
    };
}

export function mapDislikeOutcome(
    msg: ChatMessage,
    res: any,
    retryCount: number
): ChatMessage | null {
    if (res?.askForRetry) {
        msg.isLogged = true;
        return retryCount > 2
            ? {
                  isAi: true,
                  text: 'تم تسجيل المشكلة وسنعمل على حلها. يرجى محاولة طرح السؤال بطريقة أخرى.',
                  isLogged: true,
                  originalQuestion: msg.originalQuestion
              }
            : {
                  isAi: true,
                  text: res.message,
                  isLogged: true,
                  askRetry: true,
                  originalQuestion: msg.originalQuestion,
                  retryCount,
                  suggestionTips: res.suggestionTips
              };
    }

    if (res?.shouldStopRetrying) {
        msg.isLogged = true;
        msg.retryCount = 2;
        return {
            isAi: true,
            text: res.message,
            isLogged: true,
            suggestionTips: res.suggestionTips
        };
    }

    if (res?.regeneratedData) {
        msg.retryCount = retryCount;
        const newData = res.regeneratedData;
        return {
            isAi: true,
            ...(Array.isArray(newData)
                ? { tableData: newData }
                : newData.Message
                  ? { text: newData.Message }
                  : { data: newData }),
            originalQuestion: msg.originalQuestion,
            retryCount
        };
    }

    if (res?.success) {
        msg.isLogged = true;
        if (res.message) {
            return {
                isAi: true,
                text: res.message,
                isLogged: true,
                suggestionTips: res.suggestionTips
            };
        }
    }

    return null;
}

