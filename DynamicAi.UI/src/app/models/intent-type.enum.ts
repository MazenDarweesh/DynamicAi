# 🎯 IntentType Enum(Frontend TypeScript)

/**
 * Intent types for user question classification
 * Matches Backend: IntentType.cs (DynamicAi.Domain.Entities)
 * Part of Phase 1: The Brain (Intent Classification System)
 */
export enum IntentType {
    /**
     * DataRetrieval - User wants raw data, lists, specific numbers, or records
     * Example questions:
     * - "Get all customers"
     * - "Show me sales from last month"
     * - "List active invoices"
     */
    DataRetrieval = 'DataRetrieval',

    /**
     * Analysis - User wants insights, comparisons, aggregations, or 'Why' questions
     * Example questions:
     * - "Why did sales drop?"
     * - "What is the average revenue per customer?"
     * - "Compare Q1 and Q2 performance"
     */
    Analysis = 'Analysis',

    /**
     * Prediction - User asks about future forecasts or predictions
     * WITHOUT referencing specific historical data
     * Example questions:
     * - "Predict next month's sales"
     * - "Forecast revenue for Q2"
     * - "What will be the future trends?"
     */
    Prediction = 'Prediction',

    /**
     * PredictionWithDataFetch - User wants prediction based on specific historical data
     * Explicitly mentions data to use (e.g., "based on last 6 months")
     * Example questions:
     * - "Based on last 6 months, predict next month sales"
     * - "Using previous year data, forecast Q2 revenue"
     * - "From last quarter data, predict next quarter"
     */
    PredictionWithDataFetch = 'PredictionWithDataFetch',

    /**
     * Chat - Non-business interactions, greetings, thanks, or capability questions
     * Example questions:
     * - "Hello"
     * - "Thank you"
     * - "What can you do?"
     */
    Chat = 'Chat',

    /**
     * Unknown - Ambiguous or unclassifiable questions (low confidence)
     * Used when the system cannot determine the intent with sufficient confidence
     */
    Unknown = 'Unknown'
}

/**
 * Helper function to check if an intent is business-related
 */
export function isBusinessIntent(intent: IntentType): boolean {
    return intent === IntentType.DataRetrieval ||
        intent === IntentType.Analysis ||
        intent === IntentType.Prediction ||
        intent === IntentType.PredictionWithDataFetch;
}

/**
 * Helper function to get display name for intent type
 */
export function getIntentDisplayName(intent: IntentType): string {
    const displayNames: Record<IntentType, string> = {
        [IntentType.DataRetrieval]: 'Data Retrieval',
        [IntentType.Analysis]: 'Analysis',
        [IntentType.Prediction]: 'Prediction',
        [IntentType.PredictionWithDataFetch]: 'Prediction (Data-Based)',
        [IntentType.Chat]: 'Chat',
        [IntentType.Unknown]: 'Unknown'
    };
    return displayNames[intent];
}

/**
 * Helper function to get icon for intent type
 */
export function getIntentIcon(intent: IntentType): string {
    const icons: Record<IntentType, string> = {
        [IntentType.DataRetrieval]: '📊',
        [IntentType.Analysis]: '🔍',
        [IntentType.Prediction]: '🔮',
        [IntentType.PredictionWithDataFetch]: '📈',
        [IntentType.Chat]: '💬',
        [IntentType.Unknown]: '❓'
    };
    return icons[intent];
}
