namespace DynamicAi.Domain.Entities;

/// <summary>
/// Represents the review status of a pending intent example.
/// Used in the Confidence-Based Review System (Phase 2: Safety Net).
/// </summary>
public enum IntentReviewStatus
{
    /// <summary>
    /// Awaiting admin review. Not yet part of the training set.
    /// </summary>
    Pending,

    /// <summary>
    /// Approved by admin. Now part of the active training set.
    /// </summary>
    Approved,

    /// <summary>
    /// Rejected by admin. Excluded from training.
    /// </summary>
    Rejected
}
