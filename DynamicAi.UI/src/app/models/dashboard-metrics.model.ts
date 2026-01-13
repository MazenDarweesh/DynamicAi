export interface PerformanceMetrics {
    averageLatencyMs: number;
    p95LatencyMs: number;
    tokenThroughputPerSecond: number;
    totalRequests: number;
    successRate: number;
    errorRate: number;
    cacheHitRate: number;
}

export interface IntentDistribution {
    intent: string;
    count: number;
    percentage: number;
}

export interface CostBreakdown {
    totalCostUsd: number;
    costPerQueryAvg: number;
    totalInputTokens: number;
    totalOutputTokens: number;
}

export interface UserExperienceMetrics {
    averageSatisfactionScore: number;
    clarificationRate: number;
    ambiguityRate: number;
    zeroResultRate: number;
}

export interface ProblematicQuery {
    rawQuestion: string;
    errorMessage: string;
    latencyMs: number;
    occurredAt: string;
}
