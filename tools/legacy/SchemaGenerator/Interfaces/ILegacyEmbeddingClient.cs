namespace SchemaGenerator.Interfaces;

/// <summary>
/// Decoupled embedding client interface for legacy schema ingestion tools.
/// </summary>
public interface ILegacyEmbeddingClient
{
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);

    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}
