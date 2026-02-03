using DynamicAi.Application.DbConstants;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Gemini-specific Page Context Cache Service with lazy loading.
/// Uses IMemoryCache for local tracking and Gemini API for remote caching.
/// </summary>
public class GeminiPageContextCacheService : IGeminiPageContextCache
{
    #region Fields

    private readonly ILogger<GeminiPageContextCacheService> _logger;
    private readonly IGeminiCacheManager _geminiCacheManager;
    private readonly IRagRetrievalService _qdrantRetrieval;
    private readonly IMemoryCache _memoryCache;
    private readonly IConfiguration _configuration;

    #endregion

    #region Constructor

    public GeminiPageContextCacheService(
        ILogger<GeminiPageContextCacheService> logger,
        IGeminiCacheManager geminiCacheManager,
        IRagRetrievalService qdrantRetrieval,
        IMemoryCache memoryCache,
        IConfiguration configuration)
    {
        _logger = logger;
        _geminiCacheManager = geminiCacheManager;
        _qdrantRetrieval = qdrantRetrieval;
        _memoryCache = memoryCache;
        _configuration = configuration;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Gets or creates a Gemini cache for the specified page with lazy loading.
    /// </summary>
    public async Task<string?> GetOrCreateGeminiPageCacheAsync(
        int pageId,
        CancellationToken ct = default)
    {
        var cacheKey = $"gemini_page_context_{pageId}";

        // [1] Check local memory cache
        if (_memoryCache.TryGetValue<GeminiCachedPageContext>(cacheKey, out var cached))
        {
            if (cached.IsStillValid)
            {
                _logger.LogInformation("✅ Gemini Page Cache HIT for PageID {PageId}", pageId);
                return cached.GeminiCacheResourceName;
            }

            _logger.LogWarning("⚠️ Gemini cache expired for PageID {PageId}, recreating...", pageId);
        }

        // [2] Cache MISS → Build and upload to Gemini
        _logger.LogInformation("🔄 Gemini Page Cache MISS for PageID {PageId}, building...", pageId);

        try
        {
            var cacheContent = await BuildGeminiPageContextContentAsync(pageId, ct);
            var geminiResourceName = await _geminiCacheManager.GetOrCreateCacheAsync(
                content: cacheContent,
                displayName: $"DynamicAI-Page{pageId}",
                ct: ct
            );

            if (geminiResourceName == null)
            {
                _logger.LogError("❌ Failed to create Gemini cache for PageID {PageId}", pageId);
                return null;
            }

            // [3] Store in local memory cache
            var newCached = new GeminiCachedPageContext
            {
                GeminiCacheResourceName = geminiResourceName,
                ContentHash = ComputeHash(cacheContent),
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
            };

            _memoryCache.Set(cacheKey, newCached, TimeSpan.FromHours(24));

            _logger.LogInformation("✅ Gemini cache created for PageID {PageId}: {ResourceName}",
                pageId, geminiResourceName);

            return geminiResourceName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exception while creating Gemini cache for PageID {PageId}", pageId);
            return null;
        }
    }

    /// <summary>
    /// Invalidates Gemini cache for a specific page.
    /// </summary>
    public async Task InvalidateGeminiPageCacheAsync(int pageId, CancellationToken ct = default)
    {
        var cacheKey = $"gemini_page_context_{pageId}";

        if (_memoryCache.TryGetValue<GeminiCachedPageContext>(cacheKey, out var cached))
        {
            // Delete from Gemini
            await _geminiCacheManager.InvalidateCacheAsync(ct);

            // Remove from local cache
            _memoryCache.Remove(cacheKey);

            _logger.LogInformation("✅ Invalidated Gemini cache for PageID {PageId}", pageId);
        }
        else
        {
            _logger.LogDebug("No Gemini cache found for PageID {PageId}, skip invalidation", pageId);
        }
    }

    /// <summary>
    /// Invalidates ALL Gemini page caches.
    /// </summary>
    public Task InvalidateAllGeminiPageCachesAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("⚠️ Invalidating ALL Gemini page caches (not yet fully implemented - requires cache key tracking)");

        // Note: Full implementation would require tracking all cache keys
        // For now, individual page invalidations are preferred

        return Task.CompletedTask;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Builds the full page context content for Gemini caching.
    /// Includes: Static Rules + Schema Context + Full Dictionary (grouped by ListName).
    /// </summary>
    private async Task<string> BuildGeminiPageContextContentAsync(
        int pageId,
        CancellationToken ct)
    {
        _logger.LogDebug("Building Gemini cache content for PageID {PageId}", pageId);

        // Fetch FULL page context from Qdrant
        var fullContext = await _qdrantRetrieval.GetFullPageContextForLlmCachingAsync(
            pageId,
            tenantId: "ALL" // TODO: Implement proper tenant resolution
        );

        // Build structured cache content
        var cacheContent = new
        {
            StaticRules = _configuration["Prompts:StaticRules"] ?? "Static rules not configured",
            PageID = fullContext.PageId,
            PageName = fullContext.PageName,
            SchemaContext = JsonNode.Parse(fullContext.SchemaContextJson),
            DictionaryByListName = fullContext.DictionaryByListName
        };

        return System.Text.Json.JsonSerializer.Serialize(cacheContent, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    /// <summary>
    /// Computes SHA256 hash of content for change detection.
    /// </summary>
    private static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    #endregion
}
