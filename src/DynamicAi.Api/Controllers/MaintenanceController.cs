using DynamicAi.Api.Security;
using DynamicAi.Application.Interfaces;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Api.Controllers;

/// <summary>
/// Maintenance endpoints for database migrations and cleanup.
/// ⚠️ FOR DEVELOPMENT/TEST ENVIRONMENTS ONLY
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
public class MaintenanceController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<MaintenanceController> _logger;

    public MaintenanceController(
        AppDbContext dbContext,
        ILogger<MaintenanceController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Migrates legacy "Sql" intent type to "DataRetrieval" in QueryLogs.
    /// Phase 1 cleanup: Consolidates old HandlerType values.
    /// </summary>
    [HttpPost("migrate-intent-types")]
    public async Task<IActionResult> MigrateIntentTypes()
    {
        try
        {
            _logger.LogWarning("🔧 [Maintenance] Starting Intent Type migration: Sql → DataRetrieval");

            // Find all logs with HandlerType = "Sql"
            var affectedLogs = await _dbContext.QueryLogs
                .Where(q => q.HandlerType == "Sql")
                .ToListAsync();

            if (affectedLogs.Count == 0)
            {
                _logger.LogInformation("✅ [Maintenance] No legacy 'Sql' entries found. Database is clean.");
                return Ok(new
                {
                    success = true,
                    message = "No migration needed. Database is already clean.",
                    recordsUpdated = 0
                });
            }

            // Update HandlerType to "DataRetrieval"
            foreach (var log in affectedLogs)
            {
                log.HandlerType = "DataRetrieval";
            }

            var recordsUpdated = await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "✅ [Maintenance] Migration complete. Updated {Count} records from 'Sql' to 'DataRetrieval'",
                recordsUpdated);

            return Ok(new
            {
                success = true,
                message = $"Successfully migrated {recordsUpdated} records.",
                recordsUpdated,
                oldValue = "Sql",
                newValue = "DataRetrieval"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [Maintenance] Failed to migrate intent types");
            return StatusCode(500, new
            {
                success = false,
                message = "Migration failed. Check logs for details.",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Gets current intent type distribution (for verification).
    /// Shows raw database counts before normalization.
    /// </summary>
    [HttpGet("intent-type-stats")]
    public async Task<IActionResult> GetIntentTypeStats()
    {
        try
        {
            var stats = await _dbContext.QueryLogs
                .Where(q => !string.IsNullOrEmpty(q.HandlerType))
                .GroupBy(q => q.HandlerType)
                .Select(g => new
                {
                    IntentType = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToListAsync();

            return Ok(new
            {
                success = true,
                statistics = stats,
                totalRecords = stats.Sum(s => s.Count)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [Maintenance] Failed to get intent type stats");
            return StatusCode(500, new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}
