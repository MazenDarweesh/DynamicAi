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
    Task StoreCachedSqlAsync(
        string question,
        float[] embedding,
        string sql,
        int pageId,
        string tenantId,
        int companyId);
}

/// <summary>
/// Represents a cached SQL query result.
/// </summary>
public class CachedQueryResult
{
    /// <summary>
    /// The cached SQL query.
    /// </summary>
    public required string Sql { get; set; }

    /// <summary>
    /// The similarity score (0.0 to 1.0) with the original question.
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    /// The original question that generated this SQL.
    /// </summary>
    public required string OriginalQuestion { get; set; }

    /// <summary>
    /// When this result was cached.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
