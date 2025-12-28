/**
 * Request payload for like feedback endpoint
 */
export interface LikeRequest {
    queryLogId: string;
    tenantId?: number;
}

/**
 * Response from like feedback endpoint
 */
export interface LikeResponse {
    success: boolean;
    message: string;
}
