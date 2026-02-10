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
    /// Stores an intent example into the knowledge base (Qdrant).
    /// Used for both auto-seeding (Feedback Loop) and initial seeding.
    /// </summary>
    Task StoreIntentExampleAsync(
        string question,
        float[]? embedding, // Nullable: can be generated inside if missing
        IntentType intentType,
        CancellationToken cancellationToken = default,
        IntentReviewStatus status = IntentReviewStatus.Approved,
        string source = "AutoSeed",
        float confidenceScore = 0f);



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
    /// Updates the intent type of a pending example.
    /// Used by the admin to correct misclassified intents before approval.
    /// </summary>
    Task<bool> UpdateIntentTypeAsync(Guid pointId, IntentType newIntentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the underlying storage (Collection) exists and is seeded with defaults.
    /// </summary>
    Task<bool> EnsureCollectionExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes intent examples similar to the given embedding vector.
    /// Used to clean up incorrectly classified or poisoned examples.
    /// </summary>
    Task<bool> DeleteByEmbeddingSimilarityAsync(float[] embedding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds only NEW examples that don't already exist in Qdrant.
    /// Smart incremental seeding for existing collections.
    /// Returns the count of newly added examples.
    /// </summary>
    Task<int> SeedMissingExamplesAsync(CancellationToken cancellationToken = default);
}
