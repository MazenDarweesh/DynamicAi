using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for interacting with the Vector Database for runtime operations.
/// </summary>
public interface IAppVectorDbService
{
    Task<List<VectorScoredPoint>> SearchAsync(
        string collectionName,
        float[] vector,
        int limit,
        float scoreThreshold = 0f,
        CancellationToken ct = default);

    Task UpsertPointAsync(
        string collectionName,
        Guid id,
        float[] vector,
        Dictionary<string, object> payload,
        CancellationToken ct = default);

    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default);

    Task CreateCollectionAsync(string collectionName, ulong vectorSize, CancellationToken ct = default);

    Task UpdateOptimizerConfigAsync(string collectionName, int indexingThreshold, CancellationToken ct = default);

    Task<List<VectorRetrievedPoint>> ScrollAsync(
        string collectionName,
        Dictionary<string, object>? filter = null,
        int limit = 100,
        CancellationToken ct = default);

    Task UpdatePayloadAsync(
        string collectionName,
        Guid pointId,
        Dictionary<string, object> payload,
        CancellationToken ct = default);

    Task DeletePointAsync(
        string collectionName,
        Guid pointId,
        CancellationToken ct = default);

    Task CreatePayloadIndexAsync(
        string collectionName,
        string fieldName,
        VectorPayloadFieldType fieldType,
        CancellationToken ct = default);
}

