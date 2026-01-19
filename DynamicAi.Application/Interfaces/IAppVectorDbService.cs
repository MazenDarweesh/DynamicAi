using DynamicAi.Application.Models;
using Qdrant.Client.Grpc;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for interacting with the Vector Database (Qdrant) for runtime operations.
/// </summary>
public interface IAppVectorDbService
{
    /// <summary>
    /// Searches for similar vectors in the specified collection.
    /// </summary>
    Task<List<ScoredPoint>> SearchAsync(
        string collectionName,
        float[] vector,
        int limit,
        float scoreThreshold = 0f,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts a single point into the collection.
    /// </summary>
    Task UpsertPointAsync(
        string collectionName,
        Guid id,
        float[] vector,
        Dictionary<string, object> payload,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if a collection exists.
    /// </summary>
    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default);

    /// <summary>
    /// Creates a new collection with the specified vector size.
    /// </summary>
    Task CreateCollectionAsync(string collectionName, ulong vectorSize, CancellationToken ct = default);
}
