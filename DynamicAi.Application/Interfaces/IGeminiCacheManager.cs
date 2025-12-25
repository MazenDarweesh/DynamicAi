namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Manages Gemini API Context Caching for static content.
/// Uses hash-based invalidation to automatically recreate cache when content changes.
/// </summary>
public interface IGeminiCacheManager
{
    /// <summary>
    /// Gets or creates a cached content resource on Gemini servers.
    /// If the content hash matches the existing cache, returns the cached ID.
    /// If the content has changed, invalidates the old cache and creates a new one.
    /// </summary>
    /// <param name="content">The static content to cache (e.g., system rules)</param>
    /// <param name="displayName">A display name for the cached content</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The cached content ID/Name to reference in requests, or null if caching failed</returns>
    Task<string?> GetOrCreateCacheAsync(string content, string displayName = "DynamicAI-StaticRules", CancellationToken ct = default);

    /// <summary>
    /// Explicitly invalidates (deletes) the current cache.
    /// </summary>
    Task InvalidateCacheAsync(CancellationToken ct = default);

    /// <summary>
    /// Indicates whether a valid cache exists and is ready to use.
    /// </summary>
    bool IsCacheValid { get; }

    /// <summary>
    /// The current cached content ID/Name, if any.
    /// </summary>
    string? CurrentCacheId { get; }

    /// <summary>
    /// The hash of the currently cached content, for debugging.
    /// </summary>
    string? CurrentContentHash { get; }
}
