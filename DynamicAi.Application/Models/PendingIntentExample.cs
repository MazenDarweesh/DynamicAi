using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Models;

/// <summary>
/// Represents an intent example that is pending admin review.
/// Created when the Intent Router classifies with low confidence.
/// </summary>
public class PendingIntentExample
{
    /// <summary>
    /// Unique identifier (Qdrant point ID).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The original user question.
    /// </summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// The intent type as classified by the LLM.
    /// </summary>
    public IntentType ClassifiedIntent { get; set; }

    /// <summary>
    /// The confidence score from the classification (typically < 0.75).
    /// </summary>
    public float ConfidenceScore { get; set; }

    /// <summary>
    /// Current review status: Pending, Approved, or Rejected.
    /// </summary>
    public IntentReviewStatus Status { get; set; } = IntentReviewStatus.Pending;

    /// <summary>
    /// When this example was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The source of this example (e.g., "LowConfidenceVerification", "LlmFallback").
    /// </summary>
    public string Source { get; set; } = "LowConfidenceVerification";

    /// <summary>
    /// Who reviewed this example (null if pending).
    /// </summary>
    public string? ReviewedBy { get; set; }

    /// <summary>
    /// When this example was reviewed (null if pending).
    /// </summary>
    public DateTime? ReviewedAt { get; set; }
}
