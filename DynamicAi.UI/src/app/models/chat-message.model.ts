/**
 * Represents a chat message in the conversation
 */

import { QueryInsight } from './query-insight.model';

export interface ChatMessage {
    isAi: boolean;
    text?: string;
    data?: any;
    tableData?: any[];//                        tableData: Array.isArray(data) && data.length > 0 ? data : undefined,
    // Feedback fields
    originalQuestion?: string;
    cacheId?: string;
    sql?: string;
    retryCount?: number;
    isLogged?: boolean;
    askRetry?: boolean;
    // Golden Dataset fields
    queryLogId?: string;
    isLiked?: boolean;
    showSql?: boolean; // Toggle state for SQL display
    suggestionTips?: string[];
    /** Classified intent (Phase 1: The Brain). e.g. DataRetrieval, Analysis, Prediction, Chat */
    intent?: string;
    /** Query Insight Panel data */
    queryInsights?: QueryInsight;
    isRichText?: boolean; // If true, renders text as HTML/Markdown
}

// export interface QueryInsight {
//     retrievedContext?: string[];
//     reasoning?: string;
//     isReadOnly: boolean;
// }
