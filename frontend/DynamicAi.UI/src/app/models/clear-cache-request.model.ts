/**
 * Request model for clearing cached SQL entries by question similarity.
 */
export interface ClearCacheRequest {
    /**
     * The question text to match against cached entries.
     */
    question: string;

    /**
     * Optional tenant identifier (defaults to "ALL" if not specified).
     */
    tenantId?: string;

    /**
     * Optional company identifier (defaults to 0 if not specified).
     */
    companyId?: number;

    /**
     * Optional page identifier for scoped cache clearing.
     */
    pageId?: number;
}
