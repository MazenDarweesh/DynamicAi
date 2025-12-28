/**
 * Request payload for like feedback endpoint
 */
export interface LikeRequest {
    queryLogId: string;
    tenantId?: number;
}
