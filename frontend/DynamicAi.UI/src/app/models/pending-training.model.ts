/**
 * Pending training example for review
 */
export interface PendingTrainingExample {
    id: string;
    naturalQuery: string;
    generatedSql: string;
    feedbackSource: string;
    createdAt: Date;
    correctionNotes?: string;
}
