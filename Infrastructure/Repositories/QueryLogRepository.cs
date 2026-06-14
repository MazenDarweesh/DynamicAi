using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IQueryLogRepository.
/// </summary>
public class QueryLogRepository : IQueryLogRepository
{
    private readonly AppDbContext _context;

    public QueryLogRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<QueryLog?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.QueryLogs.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<QueryLog> AddAsync(QueryLog log, CancellationToken cancellationToken = default)
    {
        _context.QueryLogs.Add(log);
        await _context.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task<List<QueryLog>> GetLogsAfterAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff);

        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<(double AvgLatency, double P95Latency, double P99Latency, double ErrorRate, int TotalQueries)> GetPerformanceAggregateAsync(
        DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff);
        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        var total = await query.CountAsync(cancellationToken);
        if (total == 0) return (0, 0, 0, 0, 0);

        var avgLatency = await query.AverageAsync(q => q.LatencyMs, cancellationToken);
        var errorCount = await query.CountAsync(q => !q.WasSuccessful, cancellationToken);
        var errorRate = errorCount * 100.0 / total;

        // P95/P99: use OrderBy + Skip to get percentile values in SQL
        var p95Index = (int)(total * 0.95);
        var p99Index = (int)(total * 0.99);

        var p95 = await query.OrderBy(q => q.LatencyMs).Skip(p95Index).Select(q => q.LatencyMs).FirstOrDefaultAsync(cancellationToken);
        var p99 = await query.OrderBy(q => q.LatencyMs).Skip(p99Index).Select(q => q.LatencyMs).FirstOrDefaultAsync(cancellationToken);

        return (avgLatency, p95, p99, errorRate, total);
    }

    public async Task<List<(string Intent, int Count)>> GetIntentDistributionAggregateAsync(
        DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff);
        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        var result = await query
            .GroupBy(q => q.HandlerType ?? "Unknown")
            .Select(g => new { Intent = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        return result.Select(x => (x.Intent, x.Count)).ToList();
    }

    public async Task<(decimal TotalCost, decimal AvgCost, long TotalTokens, int TotalQueries)> GetCostAggregateAsync(
        DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff && q.GeminiCost.HasValue);
        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        var count = await query.CountAsync(cancellationToken);
        if (count == 0) return (0, 0, 0, 0);

        var totalCost = await query.SumAsync(q => q.GeminiCost ?? 0, cancellationToken);
        var totalTokens = await query.SumAsync(q => (long)(q.InputTokens ?? 0) + (q.OutputTokens ?? 0), cancellationToken);

        return (totalCost, count > 0 ? totalCost / count : 0, totalTokens, count);
    }

    public async Task<(int Likes, int Dislikes, int Ambiguous, int Total)> GetUserExperienceAggregateAsync(
        DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff);
        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        var total = await query.CountAsync(cancellationToken);
        if (total == 0) return (0, 0, 0, 0);

        var likes = await query.CountAsync(q => q.FeedbackStatus == "Like", cancellationToken);
        var dislikes = await query.CountAsync(q => q.FeedbackStatus == "Dislike", cancellationToken);
        var ambiguous = await query.CountAsync(q => q.IsAmbiguous, cancellationToken);

        return (likes, dislikes, ambiguous, total);
    }

    public async Task<List<QueryLog>> GetProblematicLogsAsync(string? tenantId, int count = 10, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs
            .Where(q => !q.WasSuccessful || q.FeedbackStatus == "Dislike" || q.IsAmbiguous)
            .OrderByDescending(q => q.CreatedAt);

        if (!string.IsNullOrEmpty(tenantId))
            query = (IOrderedQueryable<QueryLog>)query.Where(q => q.TenantId == tenantId);

        return await query.Take(count).ToListAsync(cancellationToken);
    }

    public async Task UpdateFeedbackStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        var log = await _context.QueryLogs.FindAsync(new object[] { id }, cancellationToken);
        if (log != null)
        {
            log.FeedbackStatus = status;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<HealthAggregateResult> GetHealthAggregateAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff);
        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return new HealthAggregateResult
            {
                PeriodFrom = cutoff,
                PeriodTo = DateTime.UtcNow
            };
        }

        var successful = await query.CountAsync(q => q.WasSuccessful, cancellationToken);
        var likes = await query.CountAsync(q => q.FeedbackStatus == "Like", cancellationToken);
        var dislikes = await query.CountAsync(q => q.FeedbackStatus == "Dislike", cancellationToken);
        var healing = await query.CountAsync(q => q.SelfHealingUsed, cancellationToken);
        var fallbacks = await query.CountAsync(q => q.WasFallback, cancellationToken);

        var feedbackTotal = likes + dislikes;

        return new HealthAggregateResult
        {
            TotalQueries = total,
            SuccessfulQueries = successful,
            FailedQueries = total - successful,
            SuccessRate = Math.Round(successful * 100.0 / total, 1),
            Likes = likes,
            Dislikes = dislikes,
            LikeRatio = feedbackTotal > 0 ? Math.Round(likes * 100.0 / feedbackTotal, 1) : 0,
            SelfHealingCount = healing,
            SelfHealingRate = Math.Round(healing * 100.0 / total, 1),
            FallbackCount = fallbacks,
            FallbackRate = Math.Round(fallbacks * 100.0 / total, 1),
            PeriodFrom = cutoff,
            PeriodTo = DateTime.UtcNow
        };
    }
}
