/**
 * Request payload for asking a question
 * (Updated) Supports Conversation Context for Query Condensing (Layer 0.5)
 * Matches backend AskRequest.cs
 */
export interface AskRequest {
    question: string;
    /** معرف جلسة المحادثة – يُستخدم في ConversationContext (Layer -1) */
    sessionId?: string;
    tenantId: string;
    comID: number;
    /** السؤال السابق في المحادثة (Optional) - for Query Condensing */
    previousQuestion?: string;
    /** كود SQL السابق (Optional) - for Subquery wrapping */
    previousSql?: string;
    /** الصفحة التي اختارها المستخدم (Optional) - لتجاوز البحث الدلالي في RAG */
    selectedPageContext?: { pageId: number; pageName: string } | null;
}
