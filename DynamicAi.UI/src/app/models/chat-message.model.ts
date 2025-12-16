/**
 * Represents a chat message in the conversation
 */
export interface ChatMessage {
    isAi: boolean;
    text?: string;
    data?: any;
    tableData?: any[];
    // Feedback fields
    originalQuestion?: string;
    cacheId?: string;
    sql?: string;
    retryCount?: number;
    isLogged?: boolean;
    askRetry?: boolean;
}
