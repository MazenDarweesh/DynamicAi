// Modified to match Backend DTOs exactly (ASP.NET uses camelCase in JSON by default)

export interface PerformanceMetrics {
    // Backend: AvgLatencyMs -> avgLatencyMs
    avgLatencyMs: number;
    p95LatencyMs: number;
    p99LatencyMs: number;
    errorRate: number;
    totalQueries: number;
}

/**
 * Item in Intent Distribution breakdown.
 * Matches Backend: IntentDistributionItem.cs
 */
export interface IntentDistributionItem {
    /** Type of Intent (e.g., DataRetrieval, Analysis, Prediction, Chat, Unknown) */
    intent: string;
    /** Number of queries with this intent */
    count: number;
    /** Percentage of total queries */
    percentage: number;
}

export interface CostBreakdown {
    // Backend: TotalCost, AvgCostPerQuery, TotalTokens, TotalQueries
    totalCost: number;
    avgCostPerQuery: number;
    totalTokens: number;
    totalQueries: number;
}

export interface UserExperienceMetrics {
    // Backend: LikeCount, DislikeCount, SatisfactionRate, AmbiguousCount, ClarificationRate
    likeCount: number;
    dislikeCount: number;
    satisfactionRate: number;
    ambiguousCount: number;
    clarificationRate: number;
}

export interface ProblematicQuery {
    // Backend: Id, RawQuestion, ErrorMessage, FeedbackStatus, CreatedAt, LatencyMs
    id: string;
    rawQuestion: string;
    errorMessage: string;
    feedbackStatus: string;
    createdAt: string;
    latencyMs: number;
}

