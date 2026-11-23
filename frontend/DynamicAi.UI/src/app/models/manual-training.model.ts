/**
 * Request model for manually adding training examples
 */
export interface ManualTrainingRequest {
    /**
     * Natural language query
     */
    naturalQuery: string;

    /**
     * SQL code for the query
     */
    sql: string;

    /**
     * Optional notes about this example
     */
    notes?: string;
}

/**
 * Response from feedback operations
 */
export interface FeedbackResponse {
    success: boolean;
    message: string;
}
