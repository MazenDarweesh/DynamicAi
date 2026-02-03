namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Manages per-page LLM context caching on Gemini servers with lazy loading.
/// Each page gets its own Gemini cache containing: Static Rules + Schema + Dictionary.
/// Gemini-specific implementation of context caching.
/// </summary>
public interface IGeminiPageContextCache
{
    /// <summary>
    /// Gets or creates a Gemini cached context for the specified page.
    /// Uses local memory cache for fast lookup; creates Gemini cache on first access (lazy loading).
    /// </summary>
    /// <param name="pageId">The page ID to cache context for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// The Gemini cachedContent resource name to reference in LLM requests,
    /// or null if cache creation failed (fallback to non-cached prompt).
    /// </returns>
    Task<string?> GetOrCreateGeminiPageCacheAsync(int pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates (deletes) the Gemini cache for a specific page.
    /// Called when page schema or dictionary changes.
    /// </summary>
    /// <param name="pageId">The page ID to invalidate cache for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InvalidateGeminiPageCacheAsync(int pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates ALL Gemini page caches.
    /// Called when static rules change (affects all pages).
    /// </summary>
    Task InvalidateAllGeminiPageCachesAsync(CancellationToken cancellationToken = default);
}
