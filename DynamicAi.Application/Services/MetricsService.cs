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
        var logs = await _queryLogRepo.GetLogsAfterAsync(cutoff, tenantId, cancellationToken);

        if (logs.Count == 0)
            return new PerformanceMetrics();

        var latencies = logs.Select(l => l.LatencyMs).OrderBy(l => l).ToList();
        var avgLatency = latencies.Average();
        var p95Index = (int)(latencies.Count * 0.95);
        var p99Index = (int)(latencies.Count * 0.99);
        var p95 = latencies.ElementAtOrDefault(p95Index);
        var p99 = latencies.ElementAtOrDefault(p99Index);
        var errorRate = logs.Count(l => !l.WasSuccessful) * 100.0 / logs.Count;

        _logger.LogInformation("📊 [Metrics] Performance: Avg={Avg}ms, P95={P95}ms, Errors={Err}%",
            avgLatency, p95, errorRate);

        return new PerformanceMetrics
        {
            AvgLatencyMs = avgLatency,
            P95LatencyMs = p95,
            P99LatencyMs = p99,
            ErrorRate = errorRate,
            TotalQueries = logs.Count
        };
    }

    public async Task<IntentDistribution> GetIntentDistributionAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var logs = await _queryLogRepo.GetLogsAfterAsync(cutoff, tenantId, cancellationToken);

        var sql = logs.Count(l => l.HandlerType == "SQL");
        var predict = logs.Count(l => l.HandlerType == "Predict");
        var chat = logs.Count(l => l.HandlerType == "Chat");
        var unknown = logs.Count(l => l.HandlerType != "SQL" && l.HandlerType != "Predict" && l.HandlerType != "Chat");

        return new IntentDistribution
        {
            SqlCount = sql,
            PredictCount = predict,
            ChatCount = chat,
            UnknownCount = unknown,
            TotalCount = logs.Count
        };
    }

    public async Task<CostBreakdown> GetCostBreakdownAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var logs = await _queryLogRepo.GetLogsAfterAsync(cutoff, tenantId, cancellationToken);
        var costedLogs = logs.Where(l => l.GeminiCost.HasValue).ToList();

        if (costedLogs.Count == 0)
            return new CostBreakdown();

        var totalCost = costedLogs.Sum(l => l.GeminiCost ?? 0);
        var totalTokens = costedLogs.Sum(l => (l.InputTokens ?? 0) + (l.OutputTokens ?? 0));
        var avgCost = totalCost / costedLogs.Count;

        return new CostBreakdown
        {
            TotalCost = totalCost,
            AvgCostPerQuery = avgCost,
            TotalTokens = totalTokens,
            TotalQueries = costedLogs.Count
        };
    }

    public async Task<UserExperienceMetrics> GetUserExperienceMetricsAsync(
        string? tenantId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var logs = await _queryLogRepo.GetLogsAfterAsync(cutoff, tenantId, cancellationToken);

        if (logs.Count == 0)
            return new UserExperienceMetrics();

        var likes = logs.Count(l => l.FeedbackStatus == "Like");
        var dislikes = logs.Count(l => l.FeedbackStatus == "Dislike");
        var feedbackTotal = likes + dislikes;
        var satisfactionRate = feedbackTotal > 0 ? likes * 100.0 / feedbackTotal : 0;
        var ambiguous = logs.Count(l => l.IsAmbiguous);
        var clarificationRate = ambiguous * 100.0 / logs.Count;

        _logger.LogInformation("😊 [Metrics] User Experience: Likes={Likes}, Dislikes={Dislikes}, CSAT={CSAT}%",
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
            CreatedAt = l.CreatedAt
        }).ToList();
    }
}
