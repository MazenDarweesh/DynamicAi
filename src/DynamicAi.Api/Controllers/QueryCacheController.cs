using DynamicAi.Api.Security;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers;

/// <summary>
/// Controller for managing the SQL Query Cache.
/// Provides endpoints to clear cached queries that may be stale or poisoned.
/// </summary>
[ApiController]
[Route("api/query-cache")]
[Produces("application/json")]
[Tags("Query Cache Management")]
[Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
public class QueryCacheController : ControllerBase
{
    #region Fields

    private readonly IQueryCacheService _cacheService;
    private readonly ILogger<QueryCacheController> _logger;

    #endregion

    #region Constructor

    public QueryCacheController(
        IQueryCacheService cacheService,
        ILogger<QueryCacheController> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    #endregion

    #region Endpoints

    /// <summary>
    /// Clears a cached SQL entry matching the given normalized question and scope.
    /// Use this when you need to invalidate poisoned or stale cache.
    /// </summary>
    /// <param name="request">The question and scope to match and clear from cache.</param>
    [HttpPost("clear-by-question")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearByQuestion([FromBody] ClearCacheRequest request)
    {
        _logger.LogInformation("🧹 [QueryCache] Clearing cache for question: {Question}", request.Question);

        try
        {
            var deleted = await _cacheService.DeleteByQuestionAsync(
                request.Question,
                request.TenantId ?? "ALL",
                request.CompanyId ?? 0,
                request.PageId);

            if (deleted)
            {
                _logger.LogInformation("✓ [QueryCache] Successfully cleared cache for question");
                return Ok(new { Message = "Cache entries cleared successfully", Question = request.Question });
            }

            return Ok(new { Message = "No matching cache entries found", Question = request.Question });
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

    /// <summary>
    /// Clears all cached entries for a specific page (optionally scoped by tenant and company).
    /// </summary>
    /// <param name="pageId">The page ID whose cached entries should be cleared.</param>
    /// <param name="tenantId">Optional tenant filter.</param>
    /// <param name="companyId">Optional company filter.</param>
    [HttpDelete("page/{pageId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearByPage(
        int pageId,
        [FromQuery] string? tenantId = null,
        [FromQuery] int? companyId = null)
    {
        _logger.LogInformation(
            "🧹 [QueryCache] Clearing cache for PageId={PageId}, TenantId={TenantId}, CompanyId={CompanyId}",
            pageId,
            tenantId ?? "*",
            companyId?.ToString() ?? "*");

        try
        {
            var count = await _cacheService.ClearByPageAsync(pageId, tenantId, companyId);
            return Ok(new
            {
                Message = count > 0
                    ? $"Cleared {count} cache entries for page {pageId}"
                    : $"No cache entries found for page {pageId}",
                PageId = pageId,
                DeletedCount = count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache for page {PageId}", pageId);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    #endregion
}
