using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicAi.Api.Services;

/// <summary>
/// Manages Gemini API Context Caching with hash-based invalidation.
/// Uses REST API directly for cache operations.
/// Stores static rules on Gemini servers to reduce latency by 3-5 seconds.
/// </summary>
public class GeminiCacheManager : IGeminiCacheManager
{
    private readonly ILogger<GeminiCacheManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly int _cacheTtlSeconds;

    private string? _currentCacheId;
    private string? _currentContentHash;
    private DateTime _cacheExpiresAt = DateTime.MinValue;

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public bool IsCacheValid => !string.IsNullOrEmpty(_currentCacheId) && DateTime.UtcNow < _cacheExpiresAt;
    public string? CurrentCacheId => _currentCacheId;
    public string? CurrentContentHash => _currentContentHash;

    public GeminiCacheManager(
        ILogger<GeminiCacheManager> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("GeminiCache");

        _apiKey = configuration["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Gemini:ApiKey not configured");

        _modelId = configuration["Gemini:ChatModel"] ?? "gemini-2.5-flash";

        // Parse TTL from config, default 24 hours (86400 seconds)
        var ttlHours = configuration.GetValue<int>("Gemini:CacheTtlHours", 24);
        _cacheTtlSeconds = ttlHours * 3600;

        _logger.LogInformation("[GeminiCacheManager] Initialized with model {Model}, TTL: {TTL}h", _modelId, ttlHours);
    }

    /// <summary>
    /// Gets or creates a cached content resource on Gemini servers.
    /// Uses SHA256 hash to detect content changes.
    /// </summary>
    public async Task<string?> GetOrCreateCacheAsync(
        string content,
        string displayName = "DynamicAI-StaticRules",
        CancellationToken ct = default)
    {
        var newHash = ComputeHash(content);

        _logger.LogDebug("[GeminiCacheManager] Content hash: {Hash}", newHash[..16] + "...");

        // If hash matches and cache is valid, reuse it
        if (_currentContentHash == newHash && IsCacheValid)
        {
            _logger.LogInformation("[GeminiCacheManager] ✓ Cache valid (hash match), reusing: {CacheId}", _currentCacheId);
            return _currentCacheId;
        }

        // Hash changed or cache expired - need to recreate
        if (!string.IsNullOrEmpty(_currentCacheId))
        {
            _logger.LogInformation("[GeminiCacheManager] ⟳ Content changed or expired, invalidating old cache...");
            await InvalidateCacheAsync(ct);
        }

        try
        {
            _logger.LogInformation("[GeminiCacheManager] Creating new cache for {ContentLength} chars...", content.Length);

            // Create cached content via REST API
            var requestBody = new
            {
                model = $"models/{_modelId}",
                displayName = displayName,
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = content } },
                        role = "user"
                    }
                },
                ttl = $"{_cacheTtlSeconds}s"
            };

            var url = $"{BaseUrl}/cachedContents?key={_apiKey}";

            var response = await _httpClient.PostAsJsonAsync(url, requestBody, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("[GeminiCacheManager] ✗ API returned {StatusCode}: {Error}",
                    response.StatusCode, errorContent);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CachedContentResponse>(ct);

            if (result?.Name == null)
            {
                _logger.LogError("[GeminiCacheManager] ✗ Invalid response: missing name");
                return null;
            }

            _currentCacheId = result.Name;
            _currentContentHash = newHash;
            _cacheExpiresAt = DateTime.UtcNow.AddSeconds(_cacheTtlSeconds);

            _logger.LogInformation("[GeminiCacheManager] ✓ Cache created: {CacheId}, expires at {ExpiresAt}",
                _currentCacheId, _cacheExpiresAt);

            return _currentCacheId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GeminiCacheManager] ✗ Failed to create cache: {Message}", ex.Message);

            // Clear state on failure
            _currentCacheId = null;
            _currentContentHash = null;

            return null;
        }
    }

    /// <summary>
    /// Explicitly invalidates the current cache.
    /// </summary>
    public async Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_currentCacheId))
        {
            _logger.LogDebug("[GeminiCacheManager] No cache to invalidate");
            return;
        }

        try
        {
            _logger.LogInformation("[GeminiCacheManager] Deleting cache: {CacheId}", _currentCacheId);

            var url = $"{BaseUrl}/{_currentCacheId}?key={_apiKey}";
            var response = await _httpClient.DeleteAsync(url, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[GeminiCacheManager] ✓ Cache deleted");
            }
            else
            {
                _logger.LogWarning("[GeminiCacheManager] Cache delete returned {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GeminiCacheManager] Failed to delete cache (may have expired): {Message}", ex.Message);
        }
        finally
        {
            _currentCacheId = null;
            _currentContentHash = null;
            _cacheExpiresAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Computes SHA256 hash of content for change detection.
    /// </summary>
    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    // Response DTO
    private class CachedContentResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("createTime")]
        public string? CreateTime { get; set; }

        [JsonPropertyName("expireTime")]
        public string? ExpireTime { get; set; }
    }
}
