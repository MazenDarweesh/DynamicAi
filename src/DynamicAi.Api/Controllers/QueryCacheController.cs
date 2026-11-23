using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace DynamicAi.Api.Controllers;

/// <summary>
/// Controller for managing the SQL Query Cache.
/// Provides endpoints to clear cached queries that may be stale or poisoned.
/// </summary>
[ApiController]
[Route("api/query-cache")]
[Produces("application/json")]
[Tags("Query Cache Management")]
public class QueryCacheController : ControllerBase
{
    #region Fields

    private readonly IQueryCacheService _cacheService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QueryCacheController> _logger;

    #endregion

    #region Constructor

    public QueryCacheController(
        IQueryCacheService cacheService,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILlmUsageTracker usageTracker, // Injected
        IConfiguration configuration,
        ILogger<QueryCacheController> logger)
    {
        _cacheService = cacheService;
        _embeddingGenerator = embeddingGenerator;
        _usageTracker = usageTracker;
        _configuration = configuration;
        _logger = logger;
    }

    #endregion

    #region Endpoints

    /// <summary>
    /// Clears cached SQL entries similar to the given question.
    /// Use this when you need to invalidate poisoned or stale cache.
    /// </summary>
    /// <param name="request">The question to match and clear from cache.</param>
    [HttpPost("clear-by-question")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearByQuestion([FromBody] ClearCacheRequest request)
    {
        _logger.LogInformation("🧹 [QueryCache] Clearing cache for question: {Question}", request.Question);

        try
        {
            // Generate embedding for the question
            var embedding = await _embeddingGenerator.GenerateAsync(request.Question);
            var embeddingModel = _configuration["Gemini:EmbeddingModel"] ?? "gemini-embedding-001";

            // Track Usage (Embedding)
            var embedTokens = request.Question.Length / 4;
            await _usageTracker.TrackEmbeddingCallAsync(
                LlmOperationType.Embedding, // Or SqlGeneration/Retrieval
                Guid.NewGuid().ToString(),
                embedTokens,
                request.TenantId,
                request.CompanyId,
                modelName: embeddingModel
            );

            var vector = embedding.Vector.ToArray();

            // Delete similar cache entries
            var deleted = await _cacheService.DeleteSimilarCacheAsync(
                vector,
                request.TenantId ?? "ALL",
                request.CompanyId ?? 0);

            if (deleted)
            {
                _logger.LogInformation("✓ [QueryCache] Successfully cleared cache for question");
                return Ok(new { Message = "Cache entries cleared successfully", Question = request.Question });
            }
            else
            {
                return Ok(new { Message = "No matching cache entries found", Question = request.Question });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Clears a specific cache entry by its unique ID.
    /// </summary>
    /// <param name="cacheId">The unique identifier of the cache entry to delete.</param>
    [HttpDelete("{cacheId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearById(string cacheId)
    {
        _logger.LogInformation("🧹 [QueryCache] Clearing cache entry: {CacheId}", cacheId);

        try
        {
            var deleted = await _cacheService.DeleteCacheEntryAsync(cacheId);
            return deleted
                ? Ok(new { Message = "Cache entry deleted", CacheId = cacheId })
                : NotFound(new { Error = "Cache entry not found", CacheId = cacheId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache entry");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    #endregion
}
