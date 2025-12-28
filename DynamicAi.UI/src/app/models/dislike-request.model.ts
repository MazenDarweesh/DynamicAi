/**
 * Request payload for dislike feedback endpoint
 */
export interface DislikeRequest {
    queryLogId?: string;
    userComment?: string;
    question?: string;
    cacheId?: string;
    sql?: string;
    retryCount: number;
    tenantId: string;
    companyId: number;
    displayedMessage?: string;
}

