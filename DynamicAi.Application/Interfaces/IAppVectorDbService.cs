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

    #region Phase 2: Safety Net (Admin Review)

    /// <summary>
    /// Scrolls through points in a collection with optional filtering.
    /// Used to retrieve pending examples for admin review.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="filter">Optional filter conditions (key-value pairs).</param>
    /// <param name="limit">Maximum number of points to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of retrieved points.</returns>
    Task<List<RetrievedPoint>> ScrollAsync(
        string collectionName,
        Dictionary<string, object>? filter = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the payload of an existing point.
    /// Used to change review_status from Pending to Approved.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="pointId">The point ID to update.</param>
    /// <param name="payload">The payload fields to update (merged with existing).</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdatePayloadAsync(
        string collectionName,
        Guid pointId,
        Dictionary<string, object> payload,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a point from the collection.
    /// Used to reject pending examples.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="pointId">The point ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeletePointAsync(
        string collectionName,
        Guid pointId,
        CancellationToken ct = default);


    /// <summary>
    /// Creates a payload index for optimized filtering.
    /// </summary>
    Task CreatePayloadIndexAsync(
        string collectionName,
        string fieldName,
        PayloadSchemaType fieldType,
        CancellationToken ct = default);

    #endregion
}

