namespace SchemaGenerator.Interfaces;

public enum LegacyOperationType
{
    SqlGeneration,
    Chat,
    Embedding
}

public sealed record LegacyUsageSummary(
    int TotalInputTokens,
    int TotalOutputTokens,
    decimal TotalCostUsd,
    int TotalCallCount = 0);

/// <summary>
/// Standalone usage and cost tracker contract for legacy schema tools.
/// </summary>
public interface ILegacyUsageTracker
{
    Task<decimal> TrackLlmCallAsync(
        LegacyOperationType operationType,
        string requestId,
        int inputTokens,
        int outputTokens,
        string? tenantId = null,
        string? modelName = null,
        CancellationToken cancellationToken = default);

    Task<decimal> TrackEmbeddingCallAsync(
        LegacyOperationType operationType,
        string requestId,
        int tokenCount,
        string? tenantId = null,
        string? modelName = null,
        CancellationToken cancellationToken = default);

    LegacyUsageSummary GetSessionSummary();

    void ResetSession();
}
