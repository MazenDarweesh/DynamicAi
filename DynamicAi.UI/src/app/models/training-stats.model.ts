/**
 * Training statistics for Golden Dataset
 */
export interface TrainingStats {
    /**
     * Number of verified training examples
     */
    verified: number;

    /**
     * Number of examples pending review
     */
    pendingReview: number;

    /**
     * Target number of examples for fine-tuning
     */
    targetForFineTuning: number;

    /**
     * Progress percentage toward target
     */
    progress: number;
}
