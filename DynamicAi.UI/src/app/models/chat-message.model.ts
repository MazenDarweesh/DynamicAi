/**
 * Represents a chat message in the conversation
 */

import { QueryInsight } from './query-insight.model';

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
    // Golden Dataset fields
    queryLogId?: string;
    isLiked?: boolean;
    showSql?: boolean; // Toggle state for SQL display
    suggestionTips?: string[];
    /** Classified intent (Phase 1: The Brain). e.g. DataRetrieval, Analysis, Prediction, Chat */
    intent?: string;
    /** Query Insight Panel data */
    queryInsights?: QueryInsight;
    showInsights?: boolean; // Toggle state for insights panel
}

