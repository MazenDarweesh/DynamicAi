using System.Text.Json.Serialization;

namespace DynamicAi.Application.Models;

/// <summary>
/// Response DTO for Gemini cachedContents API.
/// Maps the JSON response from: POST /v1beta/cachedContents
/// </summary>
public class GeminiCacheResponse
{
    /// <summary>
    /// Resource name (e.g., "cachedContents/abc123xyz").
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Display name for the cached content.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// When the cache was created (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("createTime")]
    public string? CreateTime { get; set; }

    /// <summary>
    /// When the cache will expire (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("expireTime")]
    public string? ExpireTime { get; set; }
}
