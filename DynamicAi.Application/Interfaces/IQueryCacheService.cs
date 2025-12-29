using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for caching SQL queries based on semantic similarity.
/// </summary>
public interface IQueryCacheService
{
    /// <summary>
    /// Attempts to retrieve a cached SQL result based on question embedding similarity.
    /// </summary>
    /// <param name="questionEmbedding">The embedding vector of the user's question.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    /// <returns>The cached result if found with similarity >= threshold, otherwise null.</returns>
    Task<CachedQueryResult?> TryGetCachedSqlAsync(
        float[] questionEmbedding,
        string tenantId,
        int companyId);

    /// <summary>
    /// Stores a successful SQL generation result in the cache.
    /// </summary>
    /// <param name="question">The original user question.</param>
    /// <param name="embedding">The question embedding vector.</param>
    /// <param name="sql">The generated SQL query.</param>
    /// <param name="pageId">The page ID used for context.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    Task<Guid> StoreCachedSqlAsync(
        Guid id,
        string question,
        float[] embedding,
        string sql,
        int pageId,
        string tenantId,
        int companyId);

    /// <summary>
    /// Deletes a cached query by its Qdrant point ID.
    /// </summary>
    /// <param name="cacheId">The Qdrant point UUID.</param>
    /// <returns>True if deletion was successful.</returns>
    Task<bool> DeleteCacheEntryAsync(string cacheId);

    /// <summary>
    /// Finds and deletes cache entries similar to the given embedding.
    /// </summary>
    /// <param name="embedding">The question embedding to match.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    /// <returns>True if any entries were deleted.</returns>
    Task<bool> DeleteSimilarCacheAsync(float[] embedding, string tenantId, int companyId);
}
