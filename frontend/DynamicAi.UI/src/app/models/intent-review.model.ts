/**
 * DTOs for Intent Review API (api/intent-review).
 * Matches backend IntentReviewItemDto, ClearIntentRequest.
 */

export interface IntentReviewItemDto {
  id: string;
  question: string;
  classifiedIntent: string;
  confidenceScore: number;
  status: string;
  createdAt: string;
  source?: string;
}

export interface ClearIntentRequest {
  question: string;
  tenantId?: string;
  companyId?: number;
}

export interface IntentReviewStats {
  pendingCount: number;
}
