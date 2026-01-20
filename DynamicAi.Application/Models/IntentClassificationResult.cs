using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Models;

/// <summary>
/// Represents the result of intent classification by the Semantic Router.
/// Contains the detected intent type, confidence score, and routing metadata.
/// </summary>
public class IntentClassificationResult
{
    /// <summary>
    /// The classified intent type.
    /// </summary>
    public IntentType Intent { get; set; } = IntentType.DataRetrieval;

    /// <summary>
    /// Confidence score from 0.0 to 1.0.
    /// Higher values indicate more certainty in the classification.
    /// </summary>
    public float ConfidenceScore { get; set; }

    /// <summary>
    /// The matched example from Qdrant that led to this classification.
    /// Useful for debugging and logging.
    /// </summary>
    public string? MatchedExample { get; set; }

    /// <summary>
    /// The Qdrant point ID of the matched example.
    /// </summary>
    public ulong? MatchedPointId { get; set; }

    /// <summary>
    /// Time taken to classify the intent in milliseconds.
    /// </summary>
    public long LatencyMs { get; set; }

    /// <summary>
    /// Creates a default DataRetrieval result (fallback).
    /// </summary>
    public static IntentClassificationResult Default() => new()
    {
        Intent = IntentType.DataRetrieval,
        ConfidenceScore = 0.5f,
        MatchedExample = null,
        LatencyMs = 0
    };

    /// <summary>
    /// Creates an Unknown result (for safety net).
    /// </summary>
    public static IntentClassificationResult Unknown(float confidence = 0.3f) => new()
    {
        Intent = IntentType.Unknown,
        ConfidenceScore = confidence,
        MatchedExample = null,
        LatencyMs = 0
    };
}
