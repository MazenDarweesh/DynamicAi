using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers;

[ApiController]
[Route("api/llm-costs")]
[Produces("application/json")]
[Tags("LLM Cost Management")]
public class LlmCostController : ControllerBase
{
    private readonly ILlmUsageLogRepository _usageRepo;
    private readonly IDailyBudgetLimitService _budgetService;
    private readonly ILogger<LlmCostController> _logger;

    public LlmCostController(
        ILlmUsageLogRepository usageRepo,
        IDailyBudgetLimitService budgetService,
        ILogger<LlmCostController> logger)
    {
        _usageRepo = usageRepo;
        _budgetService = budgetService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the detailed daily budget status.
    /// </summary>
    [HttpGet("budget-status")]
    [ProducesResponseType(typeof(BudgetInfo), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBudgetStatus()
    {
        var info = await _budgetService.GetBudgetInfoAsync();
        return Ok(info);
    }

    /// <summary>
    /// Gets total cost for a specific date (default: today).
    /// </summary>
    [HttpGet("daily-total")]
    [ProducesResponseType(typeof(decimal), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDailyTotal([FromQuery] DateTime? date, [FromQuery] string? tenantId, [FromQuery] int? companyId)
    {
        var targetDate = date ?? DateTime.UtcNow;
        var total = await _usageRepo.GetTotalCostForDateAsync(targetDate, tenantId, companyId);
        return Ok(new { Date = targetDate.ToString("yyyy-MM-dd"), TotalCostUsd = total });
    }

    /// <summary>
    /// Gets cost breakdown by operation type for a date.
    /// </summary>
    [HttpGet("by-operation")]
    [ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCostByOperation([FromQuery] DateTime? date, [FromQuery] string? tenantId, [FromQuery] int? companyId)
    {
        var targetDate = date ?? DateTime.UtcNow;
        var groups = await _usageRepo.GetOperationSummaryForDateAsync(targetDate, tenantId, companyId);
        
        // Convert to simplified DTO
        var result = groups.ToDictionary(
            k => k.Key.ToString(),
            v => new { v.Value.Count, CostUsd = v.Value.Cost }
        );

        return Ok(result);
    }

    /// <summary>
    /// Gets cost trend for the last N days (default: 7).
    /// </summary>
    [HttpGet("trend")]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTrend([FromQuery] int days = 7, [FromQuery] string? tenantId = null, [FromQuery] int? companyId = null)
    {
        if (days > 30) days = 30; // Max limit
        var trend = await _usageRepo.GetTrendAsync(days, tenantId, companyId);
        
        var result = trend.Select(t => new 
        { 
            Date = t.Date.ToString("yyyy-MM-dd"), 
            CostUsd = t.Cost 
        });

        return Ok(result);
    }

    /// <summary>
    /// Gets recent usage logs (for detailed inspection).
    /// </summary>
    [HttpGet("recent-logs")]
    [ProducesResponseType(typeof(IEnumerable<LlmUsageLog>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentLogs([FromQuery] int count = 50, [FromQuery] string? tenantId = null, [FromQuery] int? companyId = null)
    {
        if (count > 100) count = 100;
        var logs = await _usageRepo.GetRecentLogsAsync(count, tenantId, companyId);
        return Ok(logs);
    }
}
