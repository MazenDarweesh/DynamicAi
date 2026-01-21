using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Manages the knowledge base of Intent Examples.
/// Responsible for the "Continuous Improvement Loop" (Training, Reviewing, Approving).
/// </summary>
public interface IIntentExampleManagementService
{
    /// <summary>
    /// Seeds a new intent example into the knowledge base (Qdrant).
    /// Used for auto-seeding from LLM fallback or manual addition.
    /// </summary>
    Task SeedIntentExampleAsync(
        string question,
        float[] embedding, // Optional: if already computed
        IntentType intentType,
        CancellationToken cancellationToken = default,
        IntentReviewStatus status = IntentReviewStatus.Approved,
        string source = "AutoSeed");

    /// <summary>
    /// Saves a low-confidence classification result as a pending example for admin review.
    /// </summary>
    Task SavePendingExampleAsync(
        string question,
        IntentClassificationResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all examples currently pending review.
    /// </summary>
    Task<IEnumerable<PendingIntentExample>> GetPendingExamplesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending example, promoting it to active training data.
    /// </summary>
    Task<bool> ApproveExampleAsync(Guid pointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects and deletes a pending example.
    /// </summary>
    Task<bool> RejectExampleAsync(Guid pointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the underlying storage (Collection) exists and is seeded with defaults.
    /// </summary>
    Task<bool> EnsureCollectionExistsAsync(CancellationToken cancellationToken = default);
}
