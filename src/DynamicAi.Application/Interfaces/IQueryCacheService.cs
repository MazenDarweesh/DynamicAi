using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for caching generated SQL queries in memory using exact normalized question matching.
/// </summary>
public interface IQueryCacheService
{
    /// <summary>
    /// Attempts to retrieve a cached SQL result based on normalized question, tenant, company, and optional page scope.
    /// </summary>
    /// <param name="question">The user's question text.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    /// <param name="pageId">Optional page identifier for strict cache scoping.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cached result if found, otherwise null.</returns>
    Task<CachedQueryResult?> TryGetCachedSqlAsync(
        string question,
        string tenantId,
        int companyId,
        int? pageId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Stores a successful SQL generation result in the cache.
    /// </summary>
    /// <param name="id">Unique identifier for the cache entry (e.g., query log ID).</param>
    /// <param name="question">The original user question.</param>
    /// <param name="sql">The generated SQL query.</param>
    /// <param name="pageId">The page ID used for context.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    /// <param name="intent">The intent classified by the LLM (defaults to DataRetrieval).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cache entry identifier.</returns>
    Task<string> StoreCachedSqlAsync(
        Guid id,
        string question,
        string sql,
        int pageId,
        string tenantId,
        int companyId,
        string intent = "DataRetrieval",
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a cached query by its cache entry identifier.
    /// </summary>
    /// <param name="cacheId">The cache entry identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deletion was successful.</returns>
    Task<bool> DeleteCacheEntryAsync(string cacheId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a cached query matching the given question and scope.
    /// </summary>
    /// <param name="question">The user question to match.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    /// <param name="pageId">Optional page identifier for strict deletion scoping.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a matching entry was deleted.</returns>
    Task<bool> DeleteByQuestionAsync(
        string question,
        string tenantId,
        int companyId,
        int? pageId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Clears all cached entries associated with the specified page (optionally scoped by tenant and company).
    /// </summary>
    /// <param name="pageId">The page identifier to clear.</param>
    /// <param name="tenantId">Optional tenant identifier filter.</param>
    /// <param name="companyId">Optional company identifier filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of removed cache entries.</returns>
    Task<int> ClearByPageAsync(
        int pageId,
        string? tenantId = null,
        int? companyId = null,
        CancellationToken ct = default);
}
