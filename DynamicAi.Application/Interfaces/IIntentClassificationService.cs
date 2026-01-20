using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for classifying user intent using Qdrant semantic search.
/// Part of the Hybrid Router system (Phase 1: The Brain).
/// Extended with Phase 2: Safety Net (Confidence-Based Review).
/// </summary>
public interface IIntentClassificationService
{
    /// <summary>
    /// Classifies the user's intent based on semantic similarity with stored examples.
    /// </summary>
    /// <param name="userQuestion">The user's natural language question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Classification result with intent type and confidence score.</returns>
    Task<IntentClassificationResult> ClassifyIntentAsync(
        string userQuestion,
        CancellationToken cancellationToken = default);

    #region Phase 2: Safety Net (Confidence-Based Review)

    /// <summary>
    /// Saves a low-confidence example to Qdrant with Pending status for admin review.
    /// This runs asynchronously without blocking the main query flow.
    /// </summary>
    /// <param name="question">The user question.</param>
    /// <param name="result">The classification result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SavePendingExampleAsync(
        string question,
        IntentClassificationResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all pending intent examples awaiting admin review.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of pending examples.</returns>
    Task<IEnumerable<PendingIntentExample>> GetPendingExamplesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending intent example, making it part of the active training set.
    /// </summary>
    /// <param name="pointId">The Qdrant point ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if approval was successful.</returns>
    Task<bool> ApproveExampleAsync(Guid pointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a pending intent example, removing it from the training set.
    /// </summary>
    /// <param name="pointId">The Qdrant point ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if rejection was successful.</returns>
    Task<bool> RejectExampleAsync(Guid pointId, CancellationToken cancellationToken = default);

    #endregion
}
