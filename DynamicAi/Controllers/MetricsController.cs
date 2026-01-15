using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsService _metricsService;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(IMetricsService metricsService, ILogger<MetricsController> logger)
    {
        _metricsService = metricsService;
        _logger = logger;
    }

    /// <summary>
    /// الحصول على إحصائيات الأداء (Tier 1: Performance)
    /// </summary>
    [HttpGet("performance")]
    public async Task<ActionResult<PerformanceMetrics>> GetPerformanceMetrics(
        [FromQuery] string? tenantId,
        [FromQuery] int hours = 24)
    {
        var metrics = await _metricsService.GetPerformanceMetricsAsync(tenantId, TimeSpan.FromHours(hours));
        return Ok(metrics);
    }

    /// <summary>
    /// الحصول على توزيع أنواع الاستعلامات (Tier 2: Intelligence)
    /// </summary>
    [HttpGet("intent-distribution")]
    public async Task<ActionResult<List<IntentDistributionItem>>> GetIntentDistribution(
        [FromQuery] string? tenantId,
        [FromQuery] int days = 7)
    {
        var distribution = await _metricsService.GetIntentDistributionAsync(tenantId, TimeSpan.FromDays(days));
        return Ok(distribution);
    }

    /// <summary>
    /// الحصول على تفاصيل التكلفة (Tier 3: Business/Cost)
    /// </summary>
    [HttpGet("cost-breakdown")]
    public async Task<ActionResult<CostBreakdown>> GetCostBreakdown(
        [FromQuery] string? tenantId,
        [FromQuery] int days = 30)
    {
        var breakdown = await _metricsService.GetCostBreakdownAsync(tenantId, TimeSpan.FromDays(days));
        return Ok(breakdown);
    }

    /// <summary>
    /// الحصول على مقاييس تجربة المستخدم (Tier 4: Experience)
    /// </summary>
    [HttpGet("user-experience")]
    public async Task<ActionResult<UserExperienceMetrics>> GetUserExperienceMetrics(
        [FromQuery] string? tenantId,
        [FromQuery] int days = 7)
    {
        var metrics = await _metricsService.GetUserExperienceMetricsAsync(tenantId, TimeSpan.FromDays(days));
        return Ok(metrics);
    }

    /// <summary>
    /// الحصول على أكثر الاستعلامات التي سببت مشاكل (لتحسين النظام)
    /// </summary>
    [HttpGet("problematic-queries")]
    public async Task<ActionResult<List<ProblematicQuery>>> GetProblematicQueries(
        [FromQuery] string? tenantId,
        [FromQuery] int count = 10)
    {
        var queries = await _metricsService.GetTopProblematicQueriesAsync(tenantId, count);
        return Ok(queries);
    }
}
