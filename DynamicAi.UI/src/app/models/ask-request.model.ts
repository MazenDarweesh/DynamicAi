/**
 * Request payload for asking a question
 */
export interface AskRequest {
    question: string;
    tenantId: string;
    comID: number;
}
