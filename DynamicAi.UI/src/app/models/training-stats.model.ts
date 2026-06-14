/**
 * Training statistics and operational health dashboard details
 */
export interface TrainingStats {
    dataset: {
        verified: number;
        total: number;
        pendingReview: number;
        targetForFineTuning: number;
        progress: number;
    };
    health: {
        period: string;
        totalQueries: number;
        successRate: number;
        errorRate: number;
        successfulQueries: number;
        failedQueries: number;
        likes: number;
        dislikes: number;
        likeRatio: number;
        selfHealingCount: number;
        selfHealingRate: number;
        fallbackCount: number;
        fallbackRate: number;
    };
    synonyms: {
        pending: number;
        approved: number;
    };
}
