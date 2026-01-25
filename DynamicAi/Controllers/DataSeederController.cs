using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace DynamicAi.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DataSeederController : ControllerBase
    {
        private readonly IDataSeederService _dataSeederService;
        private readonly IQueryCacheService _cacheService;
        private readonly IIntentExampleManagementService _intentManager;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly ILogger<DataSeederController> _logger;

        public DataSeederController(
            IDataSeederService dataSeederService,
            IQueryCacheService cacheService,
            IIntentExampleManagementService intentManager,
            IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
            ILogger<DataSeederController> logger)
        {
            _dataSeederService = dataSeederService;
            _cacheService = cacheService;
            _intentManager = intentManager;
            _embeddingGenerator = embeddingGenerator;
            _logger = logger;
        }

        [HttpPost("seed")]
        public async Task<IActionResult> Seed([FromBody] SeedRequest request)
        {
            _logger.LogInformation("Received synthetic data seed request for topic: {Topic}", request.Topic);

            try
            {
                var result = await _dataSeederService.SeedSyntheticDataAsync(request.Topic, request.Count);
                return Ok(new { Message = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed data");
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        #region Cache Management

        /// <summary>
        /// Clears cached SQL entries similar to the given question.
        /// Use this when you need to invalidate poisoned cache.
        /// </summary>
        [HttpPost("cache/clear-by-question")]
        public async Task<IActionResult> ClearCacheByQuestion([FromBody] ClearCacheRequest request)
        {
            _logger.LogInformation("🧹 [CacheClear] Clearing cache for question: {Question}", request.Question);

            try
            {
                // Generate embedding for the question
                var embedding = await _embeddingGenerator.GenerateAsync(request.Question);
                var vector = embedding.Vector.ToArray();

                // Delete similar cache entries
                var deleted = await _cacheService.DeleteSimilarCacheAsync(
                    vector,
                    request.TenantId ?? "ALL",
                    request.CompanyId ?? 0);

                if (deleted)
                {
                    _logger.LogInformation("✓ [CacheClear] Successfully cleared cache for question");
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
        /// Clears a specific cache entry by its ID.
        /// </summary>
        [HttpDelete("cache/{cacheId}")]
        public async Task<IActionResult> ClearCacheById(string cacheId)
        {
            _logger.LogInformation("🧹 [CacheClear] Clearing cache entry: {CacheId}", cacheId);

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

        #region Intent Example Management

        /// <summary>
        /// Deletes an intent example by its question text using similarity search.
        /// Use this to clean up incorrectly classified intent examples.
        /// </summary>
        [HttpPost("intent/clear-by-question")]
        public async Task<IActionResult> ClearIntentByQuestion([FromBody] ClearIntentRequest request)
        {
            _logger.LogInformation("🧹 [IntentClear] Clearing intent example for: {Question}", request.Question);

            try
            {
                // Generate embedding
                var embedding = await _embeddingGenerator.GenerateAsync(request.Question);
                var vector = embedding.Vector.ToArray();

                // Delete similar intent examples
                var deleted = await _intentManager.DeleteByEmbeddingSimilarityAsync(vector);

                return deleted
                    ? Ok(new { Message = "Intent example cleared", Question = request.Question })
                    : Ok(new { Message = "No matching intent example found", Question = request.Question });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear intent example");
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        #endregion
    }

    #region Request Models

    public class ClearCacheRequest
    {
        public string Question { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public int? CompanyId { get; set; }
    }

    public class ClearIntentRequest
    {
        public string Question { get; set; } = string.Empty;
    }

    #endregion
}
