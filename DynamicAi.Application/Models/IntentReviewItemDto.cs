using System;

namespace DynamicAi.Application.Models;

/// <summary>
/// Data Transfer Object for displaying pending intent examples in the review UI.
/// </summary>
public class IntentReviewItemDto
{
    /// <summary>
    /// The unique ID of the point in Qdrant.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The user's question.
    /// </summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// The intent classified by the system.
    /// </summary>
    public string ClassifiedIntent { get; set; } = string.Empty;

    /// <summary>
    /// The confidence score of the classification.
    /// </summary>
    public float ConfidenceScore { get; set; }

    /// <summary>
    /// The current review status (Pending, Approved, Rejected).
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// When the example was created/logged.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
