using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services;

/// <summary>
/// خدمة حساب الـ Metrics للـ Dashboard.
/// تستعلم من QueryLogs لتوفير إحصائيات الأداء ورضا المستخدمين.
/// </summary>
public class MetricsService : IMetricsService
{
    private readonly IQueryLogRepository _queryLogRepo;
    private readonly ILogger<MetricsService> _logger;

    public MetricsService(IQueryLogRepository queryLogRepo, ILogger<MetricsService> logger)
    {
        _queryLogRepo = queryLogRepo;
        _logger = logger;
    }

    public async Task<PerformanceMetrics> GetPerformanceMetricsAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var (avgLatency, p95, p99, errorRate, totalQueries) =
            await _queryLogRepo.GetPerformanceAggregateAsync(cutoff, tenantId, cancellationToken);

        if (totalQueries == 0)
            return new PerformanceMetrics();

        _logger.LogInformation("📊 [Metrics] Performance: Avg={Avg:F0}ms, P95={P95}ms, Errors={Err:F1}%",
            avgLatency, p95, errorRate);

        return new PerformanceMetrics
        {
            AvgLatencyMs = avgLatency,
            P95LatencyMs = p95,
            P99LatencyMs = p99,
            ErrorRate = errorRate,
            TotalQueries = totalQueries
        };
    }

    /// <summary>
    /// Aggregates intent distribution by HandlerType (IntentType stored in QueryLog).
    /// Phase 1: DataRetrieval, Analysis, Prediction, PredictionWithDataFetch, Chat, Unknown.
    /// </summary>
    public async Task<List<IntentDistributionItem>> GetIntentDistributionAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var distribution = await _queryLogRepo.GetIntentDistributionAggregateAsync(cutoff, tenantId, cancellationToken);

        if (distribution.Count == 0) return new List<IntentDistributionItem>();

        var total = distribution.Sum(x => x.Count);

        var result = distribution.Select(x => new IntentDistributionItem
        {
            Intent = x.Intent,
            Count = x.Count,
            Percentage = (double)x.Count / total * 100
        }).ToList();

        _logger.LogInformation("📊 [Metrics] Intent Distribution: {Count} intents, {Total} total", result.Count, total);
        return result;
    }

    public async Task<CostBreakdown> GetCostBreakdownAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var (totalCost, avgCost, totalTokens, totalQueries) =
            await _queryLogRepo.GetCostAggregateAsync(cutoff, tenantId, cancellationToken);

        if (totalQueries == 0)
            return new CostBreakdown();

        return new CostBreakdown
        {
            TotalCost = totalCost,
            AvgCostPerQuery = avgCost,
            TotalTokens = (int)totalTokens,
            TotalQueries = totalQueries
        };
    }

    public async Task<UserExperienceMetrics> GetUserExperienceMetricsAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var (likes, dislikes, ambiguous, total) =
            await _queryLogRepo.GetUserExperienceAggregateAsync(cutoff, tenantId, cancellationToken);

        if (total == 0)
            return new UserExperienceMetrics();

        var feedbackTotal = likes + dislikes;
        var satisfactionRate = feedbackTotal > 0 ? likes * 100.0 / feedbackTotal : 0;
        var clarificationRate = ambiguous * 100.0 / total;

        _logger.LogInformation("😊 [Metrics] User Experience: Likes={Likes}, Dislikes={Dislikes}, CSAT={CSAT:F1}%",
            likes, dislikes, satisfactionRate);

        return new UserExperienceMetrics
        {
            LikeCount = likes,
            DislikeCount = dislikes,
            SatisfactionRate = satisfactionRate,
            AmbiguousCount = ambiguous,
            ClarificationRate = clarificationRate
        };
    }

    public async Task<List<ProblematicQuery>> GetTopProblematicQueriesAsync(
        string? tenantId, int count = 10, CancellationToken cancellationToken = default)
    {
        var logs = await _queryLogRepo.GetProblematicLogsAsync(tenantId, count, cancellationToken);

        return logs.Select(l => new ProblematicQuery
        {
            Id = l.Id,
            RawQuestion = l.RawQuestion,
            ErrorMessage = l.ErrorMessage,
            FeedbackStatus = l.FeedbackStatus,
            CreatedAt = l.CreatedAt,
            LatencyMs = l.LatencyMs
        }).ToList();
    }
}
