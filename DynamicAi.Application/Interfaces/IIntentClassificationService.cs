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


}
