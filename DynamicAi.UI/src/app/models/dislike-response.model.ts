/**
 * Response from dislike feedback endpoint
 */
export interface DislikeResponse {
    success: boolean;
    message: string;
    shouldStopRetrying: boolean;
    regeneratedData?: any;
    askForRetry?: boolean;
}
