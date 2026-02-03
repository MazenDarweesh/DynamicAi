namespace DynamicAi.Application.Models;

/// <summary>
/// Represents a page's context cached on Gemini servers.
/// Tracks the Gemini cachedContent resource name and expiration for local reuse.
/// </summary>
public class GeminiCachedPageContext
{
    /// <summary>
    /// The Gemini cachedContent resource name (e.g., "cachedContents/abc123xyz").
    /// Used as reference in subsequent LLM requests.
    /// </summary>
    public required string GeminiCacheResourceName { get; init; }

    /// <summary>
    /// SHA256 hash of the cached content (Static Rules + Schema + Dictionary).
    /// Used for invalidation detection if content changes.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// When this cache was created on Gemini servers (UTC).
    /// </summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this cache will expire on Gemini servers (UTC).
    /// </summary>
    public DateTime ExpiresAtUtc { get; init; }

    /// <summary>
    /// Checks if this cache is still valid (not expired).
    /// </summary>
    public bool IsStillValid => DateTime.UtcNow < ExpiresAtUtc;
}
