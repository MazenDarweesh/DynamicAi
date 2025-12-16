/**
 * Request payload for dislike feedback endpoint
 */
export interface DislikeRequest {
    question: string;
    cacheId?: string;
    sql?: string;
    retryCount: number;
    tenantId: string;
    companyId: number;
}
